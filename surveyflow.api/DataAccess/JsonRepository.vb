Imports System.Data
Imports System.Data.SqlClient
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' Generic CRUD against any entity described by EntityDescriptor.
' Receives and returns Newtonsoft JObject/JArray so the wire shape stays
' identical to what the React code sent through the Base44 SDK.
'
' Filter syntax (Mongo-ish, parsed from JSON string in query string):
'   { "field": "value" }                       → field = @p
'   { "field": { "$in": ["a","b"] } }          → field IN (@p1, @p2)
'   { "field": null }                          → field IS NULL
'
' Sort syntax (string in query string):
'   "field"           → ORDER BY field ASC
'   "-field"          → ORDER BY field DESC
'   ""/missing        → no ORDER BY
'
' Both filter keys and sort keys are validated against the entity's
' whitelist — any field not whitelisted raises ArgumentException.
'
' Every public method takes a RepoScope. For account-scoped entities a
' scoped call adds "account_id = @acct" to reads/updates/deletes and stamps
' account_id on inserts, so a row in another account behaves as not found.
Public Class JsonRepository

    Private ReadOnly _entity As EntityDescriptor

    Public Sub New(entity As EntityDescriptor)
        _entity = entity
    End Sub


    ' --- Read --------------------------------------------------------------

    Public Function Find(filter As JObject, sort As String, limit As Integer?, scope As RepoScope) As JArray
        Dim params As New List(Of SqlParameter)
        Dim whereClause = BuildWhere(filter, params, scope)
        Dim orderBy = BuildOrderBy(sort)
        Dim top = If(limit.HasValue AndAlso limit.Value > 0, $"TOP ({limit.Value})", "")

        Dim sql = $"SELECT {top} * FROM dbo.{_entity.TableName} {whereClause} {orderBy}"

        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.AddRange(params.ToArray())
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(RowToJson(rdr))
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    Public Function GetById(id As String, scope As RepoScope) As JObject
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"SELECT * FROM dbo.{_entity.TableName} WHERE id = @id{ScopeAnd(scope, cmd)}"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                Using rdr = cmd.ExecuteReader()
                    If rdr.Read() Then Return RowToJson(rdr)
                End Using
            End Using
        End Using
        Return Nothing
    End Function


    ' --- Write -------------------------------------------------------------

    Public Function Create(data As JObject, createdBy As String, scope As RepoScope) As JObject
        Dim id = data.Value(Of String)("id")
        If String.IsNullOrEmpty(id) Then
            id = Guid.NewGuid().ToString()
        End If

        Dim cols As New List(Of String) From {"id"}
        Dim placeholders As New List(Of String) From {"@id"}
        Dim params As New List(Of SqlParameter) From {New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id}}

        For Each sc In _entity.ScalarColumns
            Dim v = data(sc.JsonName)
            If v IsNot Nothing Then
                cols.Add(sc.ColumnName)
                Dim p = "@" & sc.ColumnName
                placeholders.Add(p)
                params.Add(MakeScalarParam(p, sc, v))
            End If
        Next

        For Each jc In _entity.JsonColumns
            Dim v = data(jc.JsonName)
            If v IsNot Nothing Then
                cols.Add(jc.ColumnName)
                Dim p = "@" & jc.ColumnName
                placeholders.Add(p)
                params.Add(New SqlParameter(p, SqlDbType.NVarChar, -1) With {.Value = v.ToString(Formatting.None)})
            End If
        Next

        ' Catch-all: dump the entire incoming object so any unmapped fields
        ' (type-specific question config, etc.) survive the round-trip.
        If Not String.IsNullOrEmpty(_entity.CatchAllJsonColumn) Then
            cols.Add(_entity.CatchAllJsonColumn)
            Dim p = "@" & _entity.CatchAllJsonColumn
            placeholders.Add(p)
            params.Add(New SqlParameter(p, SqlDbType.NVarChar, -1) With {.Value = data.ToString(Formatting.None)})
        End If

        ' account_id always comes from the scope, never from the payload.
        If _entity.AccountScoped Then
            If scope.IsUnscoped Then
                Throw New InvalidOperationException($"Creating a {_entity.TableName} row requires an account scope.")
            End If
            cols.Add("account_id")
            placeholders.Add("@account_id")
            params.Add(New SqlParameter("@account_id", SqlDbType.Char, 36) With {.Value = scope.AccountId})
        End If

        cols.Add("created_by")
        placeholders.Add("@created_by")
        params.Add(New SqlParameter("@created_by", SqlDbType.NVarChar, 320) With {
            .Value = If(String.IsNullOrEmpty(createdBy), CObj(DBNull.Value), createdBy)
        })

        Dim sql = $"INSERT INTO dbo.{_entity.TableName} ({String.Join(", ", cols)}) VALUES ({String.Join(", ", placeholders)})"

        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.AddRange(params.ToArray())
                cmd.ExecuteNonQuery()
            End Using
        End Using

        Return GetById(id, scope)
    End Function

    ' Returns Nothing when the row doesn't exist in scope (nothing is written).
    Public Function Update(id As String, data As JObject, scope As RepoScope) As JObject
        Dim sets As New List(Of String)
        Dim params As New List(Of SqlParameter) From {New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id}}

        For Each sc In _entity.ScalarColumns
            Dim v = data(sc.JsonName)
            If v IsNot Nothing Then
                Dim p = "@" & sc.ColumnName
                sets.Add($"{sc.ColumnName} = {p}")
                params.Add(MakeScalarParam(p, sc, v))
            End If
        Next

        For Each jc In _entity.JsonColumns
            Dim v = data(jc.JsonName)
            If v IsNot Nothing Then
                Dim p = "@" & jc.ColumnName
                sets.Add($"{jc.ColumnName} = {p}")
                params.Add(New SqlParameter(p, SqlDbType.NVarChar, -1) With {.Value = v.ToString(Formatting.None)})
            End If
        Next

        ' Catch-all: merge incoming fields with whatever's already stored
        ' so a partial PUT doesn't blow away the rest of the object.
        If Not String.IsNullOrEmpty(_entity.CatchAllJsonColumn) Then
            Dim merged = MergeCatchAll(id, data, scope)
            Dim p = "@" & _entity.CatchAllJsonColumn
            sets.Add($"{_entity.CatchAllJsonColumn} = {p}")
            params.Add(New SqlParameter(p, SqlDbType.NVarChar, -1) With {.Value = merged.ToString(Formatting.None)})
        End If

        sets.Add("updated_date = SYSUTCDATETIME()")

        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"UPDATE dbo.{_entity.TableName} SET {String.Join(", ", sets)} WHERE id = @id{ScopeAnd(scope, cmd)}"
                cmd.Parameters.AddRange(params.ToArray())
                cmd.ExecuteNonQuery()
            End Using
        End Using

        Return GetById(id, scope)
    End Function

    Public Function Delete(id As String, scope As RepoScope) As Boolean
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"DELETE FROM dbo.{_entity.TableName} WHERE id = @id{ScopeAnd(scope, cmd)}"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                Return cmd.ExecuteNonQuery() > 0
            End Using
        End Using
    End Function

    Public Function BulkCreate(items As JArray, createdBy As String, scope As RepoScope) As JArray
        ' Naive loop is fine for typical invite batches; if we ever face huge
        ' batches we can switch to SqlBulkCopy or a TVP.
        Dim out As New JArray
        For Each item As JToken In items
            If TypeOf item Is JObject Then
                out.Add(Create(CType(item, JObject), createdBy, scope))
            End If
        Next
        Return out
    End Function


    ' --- Mapping helpers ---------------------------------------------------

    Private Function RowToJson(rdr As SqlDataReader) As JObject
        Dim obj As New JObject

        ' Standard columns (always present per schema)
        For Each col In {"id", "created_date", "created_by", "updated_date"}
            obj(col) = ReadCell(rdr, col)
        Next
        If _entity.AccountScoped Then obj("account_id") = ReadCell(rdr, "account_id")

        For Each sc In _entity.ScalarColumns
            obj(sc.JsonName) = ReadCell(rdr, sc.ColumnName)
        Next

        For Each jc In _entity.JsonColumns
            Dim ord = rdr.GetOrdinal(jc.ColumnName)
            If rdr.IsDBNull(ord) Then
                obj(jc.JsonName) = JValue.CreateNull()
            Else
                obj(jc.JsonName) = JToken.Parse(rdr.GetString(ord))
            End If
        Next

        ' Catch-all: any field stored only in the catch-all column gets merged
        ' in here. Mapped columns above already populated their slots, so
        ' we skip those — the catch-all only fills gaps.
        If Not String.IsNullOrEmpty(_entity.CatchAllJsonColumn) Then
            Try
                Dim ord = rdr.GetOrdinal(_entity.CatchAllJsonColumn)
                If Not rdr.IsDBNull(ord) Then
                    Dim caught = JObject.Parse(rdr.GetString(ord))
                    For Each prop As JProperty In caught.Properties()
                        Dim existing = obj(prop.Name)
                        If existing Is Nothing OrElse existing.Type = JTokenType.Null Then
                            obj(prop.Name) = prop.Value
                        End If
                    Next
                End If
            Catch
                ' Stale row predating details_json — ignore.
            End Try
        End If

        Return obj
    End Function

    Private Function MergeCatchAll(id As String, incoming As JObject, scope As RepoScope) As JObject
        Dim existing As JObject = Nothing
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"SELECT {_entity.CatchAllJsonColumn} FROM dbo.{_entity.TableName} WHERE id = @id{ScopeAnd(scope, cmd)}"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                Dim raw = TryCast(cmd.ExecuteScalar(), String)
                If Not String.IsNullOrEmpty(raw) Then
                    Try
                        existing = JObject.Parse(raw)
                    Catch
                        existing = New JObject
                    End Try
                End If
            End Using
        End Using
        If existing Is Nothing Then existing = New JObject
        For Each prop As JProperty In incoming.Properties()
            existing(prop.Name) = prop.Value
        Next
        Return existing
    End Function

    Private Shared Function ReadCell(rdr As SqlDataReader, col As String) As JToken
        Dim ord = rdr.GetOrdinal(col)
        If rdr.IsDBNull(ord) Then Return JValue.CreateNull()
        Return JToken.FromObject(rdr.GetValue(ord))
    End Function

    Private Shared Function MakeScalarParam(name As String, sc As ScalarColumn, val As JToken) As SqlParameter
        Dim p As SqlParameter
        If sc.MaxLength > 0 Then
            p = New SqlParameter(name, sc.DbType, sc.MaxLength)
        Else
            p = New SqlParameter(name, sc.DbType)
        End If

        If val Is Nothing OrElse val.Type = JTokenType.Null Then
            p.Value = DBNull.Value
        Else
            p.Value = val.ToObject(Of Object)()
        End If
        Return p
    End Function


    ' --- Query string parsing ----------------------------------------------

    Private Function BuildWhere(filter As JObject, params As List(Of SqlParameter), scope As RepoScope) As String
        Dim parts As New List(Of String)

        If _entity.AccountScoped AndAlso Not scope.IsUnscoped Then
            parts.Add("account_id = @acct")
            params.Add(New SqlParameter("@acct", SqlDbType.Char, 36) With {.Value = scope.AccountId})
        End If

        Dim idx = 0
        If filter IsNot Nothing Then
            For Each prop As JProperty In filter.Properties()
                Dim col = prop.Name
                If Not _entity.FilterableColumns.Contains(col) Then
                    Throw New ArgumentException($"Field '{col}' is not filterable on {_entity.TableName}.")
                End If

                Dim v = prop.Value
                If TypeOf v Is JObject Then
                    Dim opObj = CType(v, JObject)
                    Dim inProp = opObj("$in")
                    If inProp IsNot Nothing AndAlso TypeOf inProp Is JArray Then
                        Dim arr = CType(inProp, JArray)
                        If arr.Count = 0 Then
                            parts.Add("1 = 0")  ' empty IN matches nothing
                        Else
                            Dim placeholders As New List(Of String)
                            For Each item In arr
                                idx += 1
                                Dim p = $"@p{idx}"
                                placeholders.Add(p)
                                params.Add(New SqlParameter(p, SqlDbType.NVarChar) With {
                                    .Value = item.ToObject(Of Object)()
                                })
                            Next
                            parts.Add($"{col} IN ({String.Join(", ", placeholders)})")
                        End If
                    Else
                        Throw New ArgumentException($"Unsupported operator on field '{col}'. Only $in is supported.")
                    End If
                ElseIf v Is Nothing OrElse v.Type = JTokenType.Null Then
                    parts.Add($"{col} IS NULL")
                Else
                    idx += 1
                    Dim p = $"@p{idx}"
                    parts.Add($"{col} = {p}")
                    params.Add(New SqlParameter(p, SqlDbType.NVarChar) With {
                        .Value = v.ToObject(Of Object)()
                    })
                End If
            Next
        End If

        If parts.Count = 0 Then Return ""
        Return "WHERE " & String.Join(" AND ", parts)
    End Function

    Private Function BuildOrderBy(sort As String) As String
        If String.IsNullOrWhiteSpace(sort) Then Return ""

        Dim desc = sort.StartsWith("-")
        Dim col = If(desc, sort.Substring(1), sort)

        If Not _entity.SortableColumns.Contains(col) Then
            Throw New ArgumentException($"Cannot sort by '{col}' on {_entity.TableName}.")
        End If

        Return "ORDER BY " & col & If(desc, " DESC", " ASC")
    End Function

    ' " AND account_id = @acct" (and adds the parameter) when the scope
    ' applies to this entity; "" otherwise.
    Private Function ScopeAnd(scope As RepoScope, cmd As SqlCommand) As String
        If Not _entity.AccountScoped OrElse scope.IsUnscoped Then Return ""
        cmd.Parameters.Add(New SqlParameter("@acct", SqlDbType.Char, 36) With {.Value = scope.AccountId})
        Return " AND account_id = @acct"
    End Function

End Class
