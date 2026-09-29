Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Contact groups (legacy Group_List / Address_Groups / Group_List_Class),
' limited to the caller's account.
'
' Membership is changed by explicit add/remove lists — legacy's save wiped
' the group and re-added only the rows visible on the current page.
'
' Routes:
'   GET    /api/ContactGroup?include_inactive=&survey_id=   → groups with member_count, survey_ids
'   GET    /api/ContactGroup/{id}
'   POST   /api/ContactGroup                  { name, description?, is_panel? }
'   PUT    /api/ContactGroup/{id}
'   DELETE /api/ContactGroup/{id}             deactivate (members kept)
'   POST   /api/ContactGroup/{id}/members         { contact_ids: [] }  → { added }
'   POST   /api/ContactGroup/{id}/members/remove  { contact_ids: [] }  → { removed }
'   POST   /api/ContactGroup/{id}/copy            { name }             → new group
'   PUT    /api/ContactGroup/{id}/surveys         { survey_ids: [] }   (empty = offered for all surveys)
' List a group's members with GET /api/Contact/search?group_id=...
Public Class ContactGroupController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.ContactGroup)

    ' survey_id: only groups offered for that survey (groups with no survey
    ' links are offered for every survey).
    <HttpGet, Route("ContactGroup")>
    Public Function List(Optional include_inactive As Boolean = False, Optional survey_id As String = Nothing) As JArray
        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT g.id, g.name, g.description, g.is_panel, g.is_active, g.copied_from_group_id,
                           g.created_date, g.created_by, g.updated_date,
                           (SELECT COUNT(*) FROM dbo.ContactGroupMembers m
                              JOIN dbo.Contacts c ON c.id = m.contact_id
                             WHERE m.group_id = g.id AND c.is_active = 1 AND c.is_hidden = 0) AS member_count,
                           (SELECT s.survey_id FROM dbo.ContactGroupSurveys s WHERE s.group_id = g.id FOR JSON PATH) AS surveys
                    FROM dbo.ContactGroups g
                    WHERE g.account_id = @a
                      AND (@inactive = 1 OR g.is_active = 1)
                      AND (@sid IS NULL
                           OR NOT EXISTS (SELECT 1 FROM dbo.ContactGroupSurveys s WHERE s.group_id = g.id)
                           OR EXISTS (SELECT 1 FROM dbo.ContactGroupSurveys s WHERE s.group_id = g.id AND s.survey_id = @sid))
                    ORDER BY g.name"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = CurrentUser.AccountId()})
                cmd.Parameters.Add(New SqlParameter("@inactive", SqlDbType.Bit) With {.Value = include_inactive})
                cmd.Parameters.Add(New SqlParameter("@sid", SqlDbType.Char, 36) With {.Value = If(CObj(ContactService.Clean(survey_id)), DBNull.Value)})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        Dim surveyIds As New JArray
                        If Not rdr.IsDBNull(10) Then
                            For Each s In JArray.Parse(rdr.GetString(10))
                                surveyIds.Add(s("survey_id"))
                            Next
                        End If
                        out.Add(New JObject From {
                            {"id", rdr.GetString(0)},
                            {"name", rdr.GetString(1)},
                            {"description", If(rdr.IsDBNull(2), Nothing, rdr.GetString(2))},
                            {"is_panel", rdr.GetBoolean(3)},
                            {"is_active", rdr.GetBoolean(4)},
                            {"copied_from_group_id", If(rdr.IsDBNull(5), Nothing, rdr.GetString(5))},
                            {"created_date", rdr.GetDateTime(6)},
                            {"created_by", If(rdr.IsDBNull(7), Nothing, rdr.GetString(7))},
                            {"updated_date", rdr.GetDateTime(8)},
                            {"member_count", rdr.GetInt32(9)},
                            {"survey_ids", surveyIds}
                        })
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    ' Current group memberships of the contacts invited to a survey — lets
    ' results be filtered / segmented / cross-tabbed by group (legacy did
    ' this with Results → Respondant → Address_Groups).
    ' → [{ contact_id, group_id, group_name }]
    <HttpGet, Route("ContactGroup/memberships")>
    Public Function MembershipsForSurvey(survey_id As String) As IHttpActionResult
        If Not Ownership.SurveyInCallerAccount(survey_id) Then Return NotFound()
        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT DISTINCT m.contact_id, g.id, g.name
                    FROM dbo.SurveyInvites i
                    JOIN dbo.ContactGroupMembers m ON m.contact_id = i.contact_id
                    JOIN dbo.ContactGroups g ON g.id = m.group_id AND g.is_active = 1 AND g.account_id = @a
                    WHERE i.survey_id = @s AND i.account_id = @a"
                cmd.Parameters.Add(New SqlParameter("@s", SqlDbType.Char, 36) With {.Value = survey_id})
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = CurrentUser.AccountId()})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(New JObject From {{"contact_id", rdr.GetString(0)}, {"group_id", rdr.GetString(1)}, {"group_name", rdr.GetString(2)}})
                    End While
                End Using
            End Using
        End Using
        Return Ok(out)
    End Function

    <HttpGet, Route("ContactGroup/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = List(include_inactive:=True).OfType(Of JObject)().FirstOrDefault(Function(g) g.Value(Of String)("id") = id)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPost, Route("ContactGroup")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        Dim name = ContactService.Clean(data?.Value(Of String)("name"))
        If name Is Nothing Then Return BadRequest("'name' is required.")
        data("name") = name
        data.Remove("is_active")
        data.Remove("copied_from_group_id")
        Try
            Dim row = Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope())
            Return GetOne(row.Value(Of String)("id"))
        Catch ex As SqlException When IsUniqueViolation(ex)
            Return NameTaken(name)
        End Try
    End Function

    <HttpPut, Route("ContactGroup/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")
        If data("name") IsNot Nothing Then
            Dim name = ContactService.Clean(data.Value(Of String)("name"))
            If name Is Nothing Then Return BadRequest("'name' can't be blank.")
            data("name") = name
        End If
        data.Remove("copied_from_group_id")
        Try
            If Repo.Update(id, data, CurrentUser.Scope()) Is Nothing Then Return NotFound()
            Return GetOne(id)
        Catch ex As SqlException When IsUniqueViolation(ex)
            Return NameTaken(data.Value(Of String)("name"))
        End Try
    End Function

    ' Soft delete: frees the name for reuse; memberships are kept so the
    ' group can be restored with PUT is_active=true.
    <HttpDelete, Route("ContactGroup/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Update(id, New JObject From {{"is_active", False}}, CurrentUser.Scope()) Is Nothing Then Return NotFound()
        Return StatusCode(HttpStatusCode.NoContent)
    End Function

    <HttpPost, Route("ContactGroup/{id}/members")>
    Public Function AddMembers(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim ids = ContactService.ReadIdList(data, "contact_ids")
        If ids Is Nothing Then Return BadRequest("'contact_ids' is required.")
        Dim accountId = CurrentUser.AccountId()
        Using conn = Db.Open()
            Using tx = conn.BeginTransaction()
                If Not GroupInAccount(conn, tx, id, accountId) Then Return NotFound()
                Dim valid = ContactsInAccount(conn, tx, ids, accountId)
                Dim added = ContactService.AddMemberships(conn, tx, {id}, valid, CurrentUser.Email())
                tx.Commit()
                Return Ok(New JObject From {{"added", added}, {"ignored", ids.Count - valid.Count}})
            End Using
        End Using
    End Function

    <HttpPost, Route("ContactGroup/{id}/members/remove")>
    Public Function RemoveMembers(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim ids = ContactService.ReadIdList(data, "contact_ids")
        If ids Is Nothing Then Return BadRequest("'contact_ids' is required.")
        Dim accountId = CurrentUser.AccountId()
        Dim removed = 0
        Using conn = Db.Open()
            Using tx = conn.BeginTransaction()
                If Not GroupInAccount(conn, tx, id, accountId) Then Return NotFound()
                For Each c In ids
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM dbo.ContactGroupMembers WHERE group_id = @g AND contact_id = @c"
                        cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = id})
                        cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = c})
                        removed += cmd.ExecuteNonQuery()
                    End Using
                Next
                Touch(conn, tx, id)
                tx.Commit()
            End Using
        End Using
        Return Ok(New JObject From {{"removed", removed}})
    End Function

    ' Copy a group and its members under a new name (legacy "Save As").
    <HttpPost, Route("ContactGroup/{id}/copy")>
    Public Function Copy(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim name = ContactService.Clean(data?.Value(Of String)("name"))
        If name Is Nothing Then Return BadRequest("'name' is required.")
        Dim source = Repo.GetById(id, CurrentUser.Scope())
        If source Is Nothing Then Return NotFound()

        Dim newId = Guid.NewGuid().ToString()
        Try
            Using conn = Db.Open()
                Using tx = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "
                            INSERT INTO dbo.ContactGroups (id, account_id, name, description, is_panel, copied_from_group_id, created_by)
                            SELECT @new, account_id, @name, description, is_panel, id, @u FROM dbo.ContactGroups WHERE id = @src;
                            INSERT INTO dbo.ContactGroupMembers (group_id, contact_id, added_by)
                            SELECT @new, contact_id, @u FROM dbo.ContactGroupMembers WHERE group_id = @src;
                            INSERT INTO dbo.ContactGroupSurveys (group_id, survey_id)
                            SELECT @new, survey_id FROM dbo.ContactGroupSurveys WHERE group_id = @src;"
                        cmd.Parameters.Add(New SqlParameter("@new", SqlDbType.Char, 36) With {.Value = newId})
                        cmd.Parameters.Add(New SqlParameter("@src", SqlDbType.Char, 36) With {.Value = id})
                        cmd.Parameters.Add(New SqlParameter("@name", SqlDbType.NVarChar, 200) With {.Value = name})
                        cmd.Parameters.Add(New SqlParameter("@u", SqlDbType.NVarChar, 320) With {.Value = CurrentUser.Email()})
                        cmd.ExecuteNonQuery()
                    End Using
                    tx.Commit()
                End Using
            End Using
        Catch ex As SqlException When IsUniqueViolation(ex)
            Return NameTaken(name)
        End Try
        Return GetOne(newId)
    End Function

    ' Which surveys this group is offered for when distributing. An empty
    ' list means every survey.
    <HttpPut, Route("ContactGroup/{id}/surveys")>
    Public Function SetSurveys(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim ids = ContactService.ReadIdList(data, "survey_ids")
        If ids Is Nothing Then Return BadRequest("'survey_ids' is required.")
        Dim accountId = CurrentUser.AccountId()
        For Each s In ids
            If Not Ownership.SurveyInCallerAccount(s) Then Return BadRequest("Unknown survey.")
        Next
        Using conn = Db.Open()
            Using tx = conn.BeginTransaction()
                If Not GroupInAccount(conn, tx, id, accountId) Then Return NotFound()
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = "DELETE FROM dbo.ContactGroupSurveys WHERE group_id = @g"
                    cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = id})
                    cmd.ExecuteNonQuery()
                End Using
                For Each s In ids
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "INSERT INTO dbo.ContactGroupSurveys (group_id, survey_id) VALUES (@g, @s)"
                        cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = id})
                        cmd.Parameters.Add(New SqlParameter("@s", SqlDbType.Char, 36) With {.Value = s})
                        cmd.ExecuteNonQuery()
                    End Using
                Next
                tx.Commit()
            End Using
        End Using
        Return GetOne(id)
    End Function


    ' --- helpers ----------------------------------------------------------

    Private Shared Function GroupInAccount(conn As SqlConnection, tx As SqlTransaction, groupId As String, accountId As String) As Boolean
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "SELECT COUNT(*) FROM dbo.ContactGroups WHERE id = @g AND account_id = @a"
            cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = groupId})
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            Return CInt(cmd.ExecuteScalar()) > 0
        End Using
    End Function

    ' The subset of ids that are contacts in the account.
    Private Shared Function ContactsInAccount(conn As SqlConnection, tx As SqlTransaction, ids As List(Of String), accountId As String) As List(Of String)
        Dim out As New List(Of String)
        For Each c In ids
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Contacts WHERE id = @c AND account_id = @a"
                cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = c})
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                If CInt(cmd.ExecuteScalar()) > 0 Then out.Add(c)
            End Using
        Next
        Return out
    End Function

    Private Shared Sub Touch(conn As SqlConnection, tx As SqlTransaction, groupId As String)
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "UPDATE dbo.ContactGroups SET updated_date = SYSUTCDATETIME() WHERE id = @g"
            cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = groupId})
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    Private Function NameTaken(name As String) As IHttpActionResult
        Return Content(HttpStatusCode.Conflict, New JObject From {
            {"reason", "duplicate"}, {"message", $"A group named '{name}' already exists."}
        })
    End Function

    Friend Shared Function IsUniqueViolation(ex As SqlException) As Boolean
        Return ex.Number = 2601 OrElse ex.Number = 2627
    End Function

End Class
