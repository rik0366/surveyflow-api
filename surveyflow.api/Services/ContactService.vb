Imports System.Data
Imports System.Data.SqlClient
Imports System.Globalization
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' Address-book rules shared by manual add/edit and bulk import, so every
' path treats contacts the same way (legacy applied different rules per
' page — see the stored-proc review in sql/09).
'
'   * A contact needs an email or an ID number.
'   * match_key identifies a contact within its account:
'       email mode      → lower(email), or "id:<id_number>" when no email
'       id_number mode  → id_number (required)
'     Only one ACTIVE contact per key (DB-enforced by a filtered unique index).
'   * Emails are compared exactly (legacy used LIKE, so '_' false-matched).
'   * Suppressed emails (account opt-out or global suppression) can't be added.
'   * Custom values must use an active field_key and match its type.
Public Module ContactService

    Public Const MaxImportRows As Integer = 50000

    Private ReadOnly EmailPattern As New Regex("^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled)

    ' Normalised contact values. Blank strings become Nothing.
    Public Class ContactValues
        Public Property FirstName As String
        Public Property LastName As String
        Public Property Email As String
        Public Property IdNumber As String
        Public Property Company As String
        Public Property Custom As New JObject
    End Class

    Public Class FieldDef
        Public Property Key As String
        Public Property Type As String
        Public Property Options As HashSet(Of String)
        Public Property SurveyIds As HashSet(Of String)   ' Nothing = all surveys
    End Class

    ' Thrown for rule violations; controllers map it to 400/409.
    Public Class ContactRuleException
        Inherits Exception
        Public ReadOnly Property Reason As String
        Public ReadOnly Property ExistingId As String
        Public Sub New(reason As String, message As String, Optional existingId As String = Nothing)
            MyBase.New(message)
            Me.Reason = reason
            Me.ExistingId = existingId
        End Sub
    End Class


    ' --- Normalisation / validation ----------------------------------------

    Public Function Clean(s As String) As String
        If s Is Nothing Then Return Nothing
        Dim t = s.Trim()
        Return If(t.Length = 0, Nothing, t)
    End Function

    Public Function FromJson(o As JObject) As ContactValues
        Dim v As New ContactValues With {
            .FirstName = Clean(o?.Value(Of String)("first_name")),
            .LastName = Clean(o?.Value(Of String)("last_name")),
            .Email = Clean(o?.Value(Of String)("email")),
            .IdNumber = Clean(o?.Value(Of String)("id_number")),
            .Company = Clean(o?.Value(Of String)("company"))
        }
        Dim c = TryCast(o?("custom"), JObject)
        If c IsNot Nothing Then v.Custom = CType(c.DeepClone(), JObject)
        Return v
    End Function

    ' Returns Nothing when valid, else a short reason for the import report.
    Public Function Problem(v As ContactValues, matchMode As String, fields As Dictionary(Of String, FieldDef)) As String
        If v.Email Is Nothing AndAlso v.IdNumber Is Nothing Then Return "Needs an email or ID number"
        If matchMode = "id_number" AndAlso v.IdNumber Is Nothing Then Return "Missing ID number"
        If v.Email IsNot Nothing Then
            If v.Email.Length > 320 Then Return "Email is too long"
            If Not EmailPattern.IsMatch(v.Email) Then Return "Invalid email"
        End If
        If v.FirstName IsNot Nothing AndAlso v.FirstName.Length > 100 Then Return "First name is too long (100 max)"
        If v.LastName IsNot Nothing AndAlso v.LastName.Length > 100 Then Return "Last name is too long (100 max)"
        If v.IdNumber IsNot Nothing AndAlso v.IdNumber.Length > 100 Then Return "ID number is too long (100 max)"
        If v.Company IsNot Nothing AndAlso v.Company.Length > 200 Then Return "Company is too long (200 max)"
        Return CleanCustom(v.Custom, fields)
    End Function

    ' Validates and normalises custom values in place (dates → yyyy-MM-dd,
    ' numbers → numeric JSON, blanks removed). Returns a reason or Nothing.
    Private Function CleanCustom(custom As JObject, fields As Dictionary(Of String, FieldDef)) As String
        For Each prop In custom.Properties().ToList()
            Dim f As FieldDef = Nothing
            If Not fields.TryGetValue(prop.Name, f) Then Return $"Unknown custom field '{prop.Name}'"
            Dim raw = If(prop.Value.Type = JTokenType.Null, Nothing, Clean(prop.Value.ToString()))
            If raw Is Nothing Then
                custom.Remove(prop.Name)
                Continue For
            End If
            Select Case f.Type
                Case "number"
                    Dim d As Double
                    If Not Double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then Return $"'{prop.Name}' must be a number"
                    custom(prop.Name) = d
                Case "date"
                    Dim dt As DateTime
                    If Not DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, dt) Then Return $"'{prop.Name}' must be a date"
                    custom(prop.Name) = dt.ToString("yyyy-MM-dd")
                Case "choice"
                    Dim match = f.Options?.FirstOrDefault(Function(o) String.Equals(o, raw, StringComparison.OrdinalIgnoreCase))
                    If match Is Nothing Then Return $"'{raw}' isn't an option for '{prop.Name}'"
                    custom(prop.Name) = match
                Case Else
                    If raw.Length > 1000 Then Return $"'{prop.Name}' is too long (1000 max)"
                    custom(prop.Name) = raw
            End Select
        Next
        Return Nothing
    End Function

    Public Function MatchKey(v As ContactValues, matchMode As String) As String
        If matchMode = "id_number" Then Return v.IdNumber
        If v.Email IsNot Nothing Then Return v.Email.ToLowerInvariant()
        Return "id:" & v.IdNumber
    End Function


    ' --- Account lookups ---------------------------------------------------

    Public Function AccountMatchMode(accountId As String) As String
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT contact_match_key FROM dbo.Accounts WHERE id = @a"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                Return If(TryCast(cmd.ExecuteScalar(), String), "email")
            End Using
        End Using
    End Function

    Public Function LoadFields(accountId As String) As Dictionary(Of String, FieldDef)
        Dim out As New Dictionary(Of String, FieldDef)(StringComparer.Ordinal)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT field_key, field_type, options_json, survey_ids_json FROM dbo.ContactFields WHERE account_id = @a AND is_active = 1"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        Dim f As New FieldDef With {.Key = rdr.GetString(0), .Type = rdr.GetString(1)}
                        If Not rdr.IsDBNull(2) Then f.Options = New HashSet(Of String)(JArray.Parse(rdr.GetString(2)).Select(Function(t) t.ToString()))
                        If Not rdr.IsDBNull(3) Then f.SurveyIds = New HashSet(Of String)(JArray.Parse(rdr.GetString(3)).Select(Function(t) t.ToString()), StringComparer.OrdinalIgnoreCase)
                        out(f.Key) = f
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    ' Emails the account must not add or contact: its opt-outs + global suppression.
    Public Function SuppressedEmails(accountId As String) As HashSet(Of String)
        Dim out As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT email FROM dbo.EmailSuppression
                    UNION
                    SELECT email FROM dbo.EmailBlacklist WHERE account_id = @a"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(rdr.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    ' Active contact with this key in the account, excluding one id.
    Private Function FindActiveByKey(conn As SqlConnection, tx As SqlTransaction, accountId As String, key As String, excludeId As String) As String
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "SELECT TOP 1 id FROM dbo.Contacts WHERE account_id = @a AND match_key = @k AND is_active = 1 AND (@x IS NULL OR id <> @x)"
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            cmd.Parameters.Add(New SqlParameter("@k", SqlDbType.NVarChar, 320) With {.Value = key})
            cmd.Parameters.Add(New SqlParameter("@x", SqlDbType.Char, 36) With {.Value = If(CObj(excludeId), DBNull.Value)})
            Return TryCast(cmd.ExecuteScalar(), String)
        End Using
    End Function


    ' --- Single contact create / update ------------------------------------

    ' Creates a contact and returns its id. groupIds (optional) are added as
    ' memberships. Throws ContactRuleException on rule violations.
    Public Function Create(accountId As String, body As JObject, userEmail As String) As String
        Dim mode = AccountMatchMode(accountId)
        Dim fields = LoadFields(accountId)
        Dim v = FromJson(body)
        Dim p = Problem(v, mode, fields)
        If p IsNot Nothing Then Throw New ContactRuleException("invalid", p)
        If v.Email IsNot Nothing AndAlso Ownership.IsSuppressed(accountId, v.Email) Then
            Throw New ContactRuleException("suppressed", $"{v.Email} has opted out or is on the do-not-send list.")
        End If
        Dim key = MatchKey(v, mode)
        Dim groupIds = ReadIdList(body, "group_ids")

        Using conn = Db.Open()
            Using tx = conn.BeginTransaction()
                Dim dup = FindActiveByKey(conn, tx, accountId, key, Nothing)
                If dup IsNot Nothing Then Throw DuplicateError(mode, dup)
                CheckGroups(conn, tx, accountId, groupIds)

                Dim id = Guid.NewGuid().ToString()
                InsertContact(conn, tx, id, accountId, v, key, userEmail)
                AddSystemNote(conn, tx, accountId, id, "Contact created.", userEmail)
                AddMemberships(conn, tx, groupIds, {id}, userEmail)
                tx.Commit()
                Return id
            End Using
        End Using
    End Function

    ' Partial update: keys present in the body replace stored values (null or
    ' "" clears). group_ids, when present, becomes the contact's exact set of
    ' groups. is_active=false deactivates; true reactivates (re-checking the
    ' duplicate rule). Email changes flow to the contact's unfinished invites.
    Public Sub Update(accountId As String, id As String, body As JObject, userEmail As String)
        Dim mode = AccountMatchMode(accountId)
        Dim fields = LoadFields(accountId)

        Using conn = Db.Open()
            Using tx = conn.BeginTransaction()
                Dim current = LoadContact(conn, tx, accountId, id)
                If current Is Nothing Then Throw New ContactRuleException("not_found", "Contact not found.")

                Dim merged = CType(current.DeepClone(), JObject)
                For Each k In {"first_name", "last_name", "email", "id_number", "company"}
                    If body(k) IsNot Nothing Then merged(k) = body(k)
                Next

                ' Custom values merge key by key: keys sent replace (null or ""
                ' removes), keys not sent are kept — including values for
                ' fields that have since been deactivated. Only the keys sent
                ' are validated.
                Dim customOut = If(TryCast(current("custom"), JObject)?.DeepClone(), New JObject)
                Dim incoming = TryCast(body("custom"), JObject)
                Dim customIn As New JObject
                If incoming IsNot Nothing Then
                    For Each prop In incoming.Properties()
                        Dim raw = If(prop.Value.Type = JTokenType.Null, Nothing, Clean(prop.Value.ToString()))
                        If raw Is Nothing Then
                            CType(customOut, JObject).Remove(prop.Name)
                        Else
                            customIn(prop.Name) = prop.Value
                        End If
                    Next
                End If

                Dim v = FromJson(merged)
                v.Custom = customIn
                Dim p = Problem(v, mode, fields)
                If p IsNot Nothing Then Throw New ContactRuleException("invalid", p)
                For Each prop In customIn.Properties()
                    CType(customOut, JObject)(prop.Name) = prop.Value
                Next
                v.Custom = CType(customOut, JObject)
                merged("custom") = customOut

                Dim oldEmail = Clean(current.Value(Of String)("email"))
                Dim emailChanged = Not String.Equals(oldEmail, v.Email, StringComparison.OrdinalIgnoreCase)
                If emailChanged AndAlso v.Email IsNot Nothing AndAlso Ownership.IsSuppressed(accountId, v.Email) Then
                    Throw New ContactRuleException("suppressed", $"{v.Email} has opted out or is on the do-not-send list.")
                End If

                Dim wasActive = current.Value(Of Boolean)("is_active")
                Dim isActive = If(body("is_active") IsNot Nothing AndAlso body("is_active").Type <> JTokenType.Null, body.Value(Of Boolean)("is_active"), wasActive)
                Dim key = MatchKey(v, mode)
                If isActive Then
                    Dim dup = FindActiveByKey(conn, tx, accountId, key, id)
                    If dup IsNot Nothing Then Throw DuplicateError(mode, dup)
                End If

                Dim groupIds = ReadIdList(body, "group_ids")
                CheckGroups(conn, tx, accountId, groupIds)

                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = "
                        UPDATE dbo.Contacts SET
                            first_name = @fn, last_name = @ln, email = @em, id_number = @idn, company = @co,
                            custom_json = @cj, match_key = @k, is_active = @act,
                            deactivated_at = CASE WHEN @act = 1 THEN NULL WHEN is_active = 1 THEN SYSUTCDATETIME() ELSE deactivated_at END,
                            updated_date = SYSUTCDATETIME()
                        WHERE id = @id AND account_id = @a"
                    AddValueParams(cmd, v, key)
                    cmd.Parameters.Add(New SqlParameter("@act", SqlDbType.Bit) With {.Value = isActive})
                    cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                    cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                    cmd.ExecuteNonQuery()
                End Using

                If emailChanged Then SyncInviteEmail(conn, tx, accountId, id, v.Email)
                RefreshPendingInvites(conn, tx, accountId, id, v, fields)

                Dim changes = DescribeChanges(current, merged, wasActive, isActive)
                If changes IsNot Nothing Then AddSystemNote(conn, tx, accountId, id, changes, userEmail)

                If groupIds IsNot Nothing Then SetContactGroups(conn, tx, accountId, id, groupIds, userEmail)
                tx.Commit()
            End Using
        End Using
    End Sub

    Private Function DuplicateError(mode As String, existingId As String) As ContactRuleException
        Dim what = If(mode = "id_number", "ID number", "email")
        Return New ContactRuleException("duplicate", $"Another active contact already has this {what}.", existingId)
    End Function

    Private Function DescribeChanges(before As JObject, after As JObject, wasActive As Boolean, isActive As Boolean) As String
        Dim parts As New List(Of String)
        For Each k In {"first_name", "last_name", "email", "id_number", "company"}
            Dim a = Clean(before.Value(Of String)(k))
            Dim b = Clean(after.Value(Of String)(k))
            If Not String.Equals(a, b, StringComparison.Ordinal) Then
                parts.Add($"{k.Replace("_", " ")}: '{If(a, "")}' → '{If(b, "")}'")
            End If
        Next
        Dim customOf = Function(o As JObject) If(TryCast(o("custom"), JObject), New JObject)
        If Not JToken.DeepEquals(customOf(before), customOf(after)) Then parts.Add("custom fields updated")
        If wasActive AndAlso Not isActive Then parts.Add("deactivated")
        If Not wasActive AndAlso isActive Then parts.Add("reactivated")
        If parts.Count = 0 Then Return Nothing
        Return "Updated — " & String.Join("; ", parts)
    End Function

    Private Function LoadContact(conn As SqlConnection, tx As SqlTransaction, accountId As String, id As String) As JObject
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "SELECT first_name, last_name, email, id_number, company, custom_json, is_active FROM dbo.Contacts WITH (UPDLOCK) WHERE id = @id AND account_id = @a"
            cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            Using rdr = cmd.ExecuteReader()
                If Not rdr.Read() Then Return Nothing
                Return New JObject From {
                    {"first_name", If(rdr.IsDBNull(0), Nothing, rdr.GetString(0))},
                    {"last_name", If(rdr.IsDBNull(1), Nothing, rdr.GetString(1))},
                    {"email", If(rdr.IsDBNull(2), Nothing, rdr.GetString(2))},
                    {"id_number", If(rdr.IsDBNull(3), Nothing, rdr.GetString(3))},
                    {"company", If(rdr.IsDBNull(4), Nothing, rdr.GetString(4))},
                    {"custom", If(rdr.IsDBNull(5), JValue.CreateNull(), JToken.Parse(rdr.GetString(5)))},
                    {"is_active", rdr.GetBoolean(6)}
                }
            End Using
        End Using
    End Function


    ' --- Bulk import ---------------------------------------------------------
    '
    ' Rules (legacy sp_ProcessGroupUpload, minus its silent drops):
    '   * the account's match key decides "same contact"
    '   * rows failing validation, suppressed emails, and earlier duplicates
    '     within the file are skipped WITH a reason (last row in file wins)
    '   * existing contacts: non-blank values overwrite, blanks never erase;
    '     update_existing=false leaves them untouched
    '   * group membership is additive (never removes anyone)
    '   * dry_run=true computes the same report without writing
    Public Function Import(accountId As String, rows As JArray, groupId As String,
                           updateExisting As Boolean, dryRun As Boolean, userEmail As String) As JObject
        Dim mode = AccountMatchMode(accountId)
        Dim fields = LoadFields(accountId)
        Dim suppressed = SuppressedEmails(accountId)

        ' Existing active contacts by key.
        Dim existing As New Dictionary(Of String, JObject)(StringComparer.Ordinal)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT id, match_key, first_name, last_name, email, id_number, company, custom_json FROM dbo.Contacts WHERE account_id = @a AND is_active = 1"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        existing(rdr.GetString(1)) = New JObject From {
                            {"id", rdr.GetString(0)},
                            {"first_name", If(rdr.IsDBNull(2), Nothing, rdr.GetString(2))},
                            {"last_name", If(rdr.IsDBNull(3), Nothing, rdr.GetString(3))},
                            {"email", If(rdr.IsDBNull(4), Nothing, rdr.GetString(4))},
                            {"id_number", If(rdr.IsDBNull(5), Nothing, rdr.GetString(5))},
                            {"company", If(rdr.IsDBNull(6), Nothing, rdr.GetString(6))},
                            {"custom", If(rdr.IsDBNull(7), New JObject, JObject.Parse(rdr.GetString(7)))}
                        }
                    End While
                End Using
            End Using
        End Using

        ' Pass 1: validate, key, remember the last row per key.
        Dim parsed As New List(Of (RowNo As Integer, V As ContactValues, Key As String, Problem As String))
        Dim lastRowForKey As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        For i = 0 To rows.Count - 1
            Dim o = TryCast(rows(i), JObject)
            Dim rowNo = If(o?.Value(Of Integer?)("row"), i + 1)
            Dim v = FromJson(o)
            Dim p = Problem(v, mode, fields)
            Dim key = If(p Is Nothing, MatchKey(v, mode), Nothing)
            If key IsNot Nothing Then lastRowForKey(key) = rowNo
            parsed.Add((rowNo, v, key, p))
        Next

        ' Pass 2: decide each row.
        Dim skipped As New JArray
        Dim inserts As New List(Of (Id As String, V As ContactValues, Key As String))
        Dim updates As New List(Of (Id As String, V As ContactValues, Key As String, Changes As String))
        Dim memberIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim unchanged = 0

        For Each r In parsed
            Dim reason As String = r.Problem
            If reason Is Nothing AndAlso lastRowForKey(r.Key) <> r.RowNo Then reason = $"Duplicate in file (row {lastRowForKey(r.Key)} used)"
            If reason Is Nothing AndAlso r.V.Email IsNot Nothing AndAlso suppressed.Contains(r.V.Email) Then reason = "Opted out / do-not-send"
            If reason IsNot Nothing Then
                skipped.Add(New JObject From {{"row", r.RowNo}, {"email", r.V.Email}, {"id_number", r.V.IdNumber}, {"reason", reason}})
                Continue For
            End If

            Dim cur As JObject = Nothing
            If existing.TryGetValue(r.Key, cur) Then
                Dim id = cur.Value(Of String)("id")
                memberIds.Add(id)
                Dim merged As ContactValues = Nothing
                Dim changes = If(updateExisting, MergeNonBlank(cur, r.V, merged), Nothing)
                If changes Is Nothing Then
                    unchanged += 1
                Else
                    updates.Add((id, merged, MatchKey(merged, mode), changes))
                End If
            Else
                Dim id = Guid.NewGuid().ToString()
                inserts.Add((id, r.V, r.Key))
                memberIds.Add(id)
            End If
        Next

        Dim groupAdded = 0
        If Not dryRun AndAlso (inserts.Count > 0 OrElse updates.Count > 0 OrElse (groupId IsNot Nothing AndAlso memberIds.Count > 0)) Then
            Using conn = Db.Open()
                Using tx = conn.BeginTransaction()
                    For Each ins In inserts
                        InsertContact(conn, tx, ins.Id, accountId, ins.V, ins.Key, userEmail)
                        AddSystemNote(conn, tx, accountId, ins.Id, "Contact created by import.", userEmail)
                    Next
                    For Each up In updates
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "
                                UPDATE dbo.Contacts SET
                                    first_name = @fn, last_name = @ln, email = @em, id_number = @idn, company = @co,
                                    custom_json = @cj, match_key = @k, updated_date = SYSUTCDATETIME()
                                WHERE id = @id AND account_id = @a"
                            AddValueParams(cmd, up.V, up.Key)
                            cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = up.Id})
                            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                            cmd.ExecuteNonQuery()
                        End Using
                        SyncInviteEmail(conn, tx, accountId, up.Id, up.V.Email)
                        RefreshPendingInvites(conn, tx, accountId, up.Id, up.V, fields)
                        AddSystemNote(conn, tx, accountId, up.Id, "Updated by import — " & up.Changes, userEmail)
                    Next
                    If groupId IsNot Nothing Then groupAdded = AddMemberships(conn, tx, {groupId}, memberIds, userEmail)
                    tx.Commit()
                End Using
            End Using
        ElseIf dryRun AndAlso groupId IsNot Nothing Then
            groupAdded = CountNewMemberships(groupId, memberIds)
        End If

        Return New JObject From {
            {"dry_run", dryRun},
            {"total_rows", rows.Count},
            {"added", inserts.Count},
            {"updated", updates.Count},
            {"unchanged", unchanged},
            {"skipped", skipped.Count},
            {"added_to_group", groupAdded},
            {"skipped_rows", skipped}
        }
    End Function

    ' Non-blank incoming values overwrite; returns a change description, or
    ' Nothing when nothing would change. merged receives the resulting values.
    Private Function MergeNonBlank(cur As JObject, incoming As ContactValues, ByRef merged As ContactValues) As String
        merged = New ContactValues With {
            .FirstName = If(incoming.FirstName, Clean(cur.Value(Of String)("first_name"))),
            .LastName = If(incoming.LastName, Clean(cur.Value(Of String)("last_name"))),
            .Email = If(incoming.Email, Clean(cur.Value(Of String)("email"))),
            .IdNumber = If(incoming.IdNumber, Clean(cur.Value(Of String)("id_number"))),
            .Company = If(incoming.Company, Clean(cur.Value(Of String)("company"))),
            .Custom = CType(cur("custom").DeepClone(), JObject)
        }
        For Each prop In incoming.Custom.Properties()
            merged.Custom(prop.Name) = prop.Value
        Next

        Dim parts As New List(Of String)
        Dim cmp = Sub(label As String, a As String, b As String)
                      If Not String.Equals(a, b, StringComparison.Ordinal) Then parts.Add($"{label}: '{If(a, "")}' → '{If(b, "")}'")
                  End Sub
        cmp("first name", Clean(cur.Value(Of String)("first_name")), merged.FirstName)
        cmp("last name", Clean(cur.Value(Of String)("last_name")), merged.LastName)
        cmp("email", Clean(cur.Value(Of String)("email")), merged.Email)
        cmp("id number", Clean(cur.Value(Of String)("id_number")), merged.IdNumber)
        cmp("company", Clean(cur.Value(Of String)("company")), merged.Company)
        If Not JToken.DeepEquals(cur("custom"), merged.Custom) Then parts.Add("custom fields updated")
        If parts.Count = 0 Then Return Nothing
        Return String.Join("; ", parts)
    End Function


    ' --- Groups ----------------------------------------------------------------

    Public Function ReadIdList(body As JObject, key As String) As List(Of String)
        Dim arr = TryCast(body?(key), JArray)
        If arr Is Nothing Then Return Nothing
        Return arr.Select(Function(t) t.ToString()).Where(Function(s) s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    ' Throws unless every id is an active group in the account.
    Public Sub CheckGroups(conn As SqlConnection, tx As SqlTransaction, accountId As String, groupIds As IEnumerable(Of String))
        If groupIds Is Nothing Then Return
        For Each g In groupIds
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.ContactGroups WHERE id = @g AND account_id = @a AND is_active = 1"
                cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = g})
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                If CInt(cmd.ExecuteScalar()) = 0 Then Throw New ContactRuleException("invalid", "Unknown group.")
            End Using
        Next
    End Sub

    ' Adds each contact to each group (skipping existing memberships).
    ' Callers must have verified groups and contacts belong to the account.
    Public Function AddMemberships(conn As SqlConnection, tx As SqlTransaction, groupIds As IEnumerable(Of String),
                                   contactIds As IEnumerable(Of String), userEmail As String) As Integer
        If groupIds Is Nothing Then Return 0
        Dim added = 0
        For Each g In groupIds
            For Each c In contactIds
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = "
                        IF NOT EXISTS (SELECT 1 FROM dbo.ContactGroupMembers WHERE group_id = @g AND contact_id = @c)
                            INSERT INTO dbo.ContactGroupMembers (group_id, contact_id, added_by) VALUES (@g, @c, @u)"
                    cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = g})
                    cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = c})
                    cmd.Parameters.Add(New SqlParameter("@u", SqlDbType.NVarChar, 320) With {.Value = If(CObj(userEmail), DBNull.Value)})
                    added += Math.Max(0, cmd.ExecuteNonQuery())
                End Using
            Next
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "UPDATE dbo.ContactGroups SET updated_date = SYSUTCDATETIME() WHERE id = @g"
                cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = g})
                cmd.ExecuteNonQuery()
            End Using
        Next
        Return added
    End Function

    Private Function CountNewMemberships(groupId As String, contactIds As HashSet(Of String)) As Integer
        Dim already As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT contact_id FROM dbo.ContactGroupMembers WHERE group_id = @g"
                cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = groupId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        already.Add(rdr.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return contactIds.Where(Function(c) Not already.Contains(c)).Count()
    End Function

    ' Makes groupIds the contact's exact set of (active) groups.
    Private Sub SetContactGroups(conn As SqlConnection, tx As SqlTransaction, accountId As String, contactId As String,
                                 groupIds As List(Of String), userEmail As String)
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            Dim keep = If(groupIds.Count = 0, "", " AND m.group_id NOT IN (" & String.Join(", ", groupIds.Select(Function(g, i) "@g" & i)) & ")")
            cmd.CommandText = $"
                DELETE m FROM dbo.ContactGroupMembers m
                JOIN dbo.ContactGroups g ON g.id = m.group_id AND g.account_id = @a
                WHERE m.contact_id = @c{keep}"
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = contactId})
            For i = 0 To groupIds.Count - 1
                cmd.Parameters.Add(New SqlParameter("@g" & i, SqlDbType.Char, 36) With {.Value = groupIds(i)})
            Next
            cmd.ExecuteNonQuery()
        End Using
        AddMemberships(conn, tx, groupIds, {contactId}, userEmail)
    End Sub


    ' --- Low-level writes ------------------------------------------------------

    Private Sub InsertContact(conn As SqlConnection, tx As SqlTransaction, id As String, accountId As String,
                              v As ContactValues, key As String, userEmail As String)
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "
                INSERT INTO dbo.Contacts (id, account_id, first_name, last_name, email, id_number, company, custom_json, match_key, created_by)
                VALUES (@id, @a, @fn, @ln, @em, @idn, @co, @cj, @k, @u)"
            AddValueParams(cmd, v, key)
            cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            cmd.Parameters.Add(New SqlParameter("@u", SqlDbType.NVarChar, 320) With {.Value = If(CObj(userEmail), DBNull.Value)})
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    Private Sub AddValueParams(cmd As SqlCommand, v As ContactValues, key As String)
        Dim s = Function(x As String) If(CObj(x), DBNull.Value)
        cmd.Parameters.Add(New SqlParameter("@fn", SqlDbType.NVarChar, 100) With {.Value = s(v.FirstName)})
        cmd.Parameters.Add(New SqlParameter("@ln", SqlDbType.NVarChar, 100) With {.Value = s(v.LastName)})
        cmd.Parameters.Add(New SqlParameter("@em", SqlDbType.NVarChar, 320) With {.Value = s(v.Email)})
        cmd.Parameters.Add(New SqlParameter("@idn", SqlDbType.NVarChar, 100) With {.Value = s(v.IdNumber)})
        cmd.Parameters.Add(New SqlParameter("@co", SqlDbType.NVarChar, 200) With {.Value = s(v.Company)})
        cmd.Parameters.Add(New SqlParameter("@cj", SqlDbType.NVarChar, -1) With {
            .Value = If(v.Custom Is Nothing OrElse v.Custom.Count = 0, CObj(DBNull.Value), v.Custom.ToString(Formatting.None))
        })
        cmd.Parameters.Add(New SqlParameter("@k", SqlDbType.NVarChar, 320) With {.Value = key})
    End Sub

    ' Keep the contact's unfinished invites deliverable after an email change
    ' (legacy created a duplicate invite instead).
    Private Sub SyncInviteEmail(conn As SqlConnection, tx As SqlTransaction, accountId As String, contactId As String, email As String)
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "
                UPDATE dbo.SurveyInvites SET email = @em, updated_date = SYSUTCDATETIME()
                WHERE contact_id = @id AND account_id = @a AND status <> 'completed'
                  AND ISNULL(email, '') <> ISNULL(@em, '')"
            cmd.Parameters.Add(New SqlParameter("@em", SqlDbType.NVarChar, 320) With {.Value = If(CObj(email), DBNull.Value)})
            cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = contactId})
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    ' Invites copy the contact's details when created, so reports stay stable
    ' once people have been emailed. Invites not yet sent (status 'pending')
    ' are refreshed instead, so fixing a typo before sending reaches the email.
    ' Custom values follow the same per-survey field rules as invite creation.
    Private Sub RefreshPendingInvites(conn As SqlConnection, tx As SqlTransaction, accountId As String, contactId As String,
                                      v As ContactValues, fields As Dictionary(Of String, FieldDef))
        Dim pending As New List(Of (Id As String, SurveyId As String))
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "SELECT id, survey_id FROM dbo.SurveyInvites WHERE contact_id = @c AND account_id = @a AND status = 'pending'"
            cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = contactId})
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            Using rdr = cmd.ExecuteReader()
                While rdr.Read()
                    pending.Add((rdr.GetString(0), rdr.GetString(1)))
                End While
            End Using
        End Using

        For Each inv In pending
            Dim snapshot As New JObject
            For Each f In fields.Values
                If f.SurveyIds IsNot Nothing AndAlso Not f.SurveyIds.Contains(inv.SurveyId) Then Continue For
                Dim val = v.Custom?(f.Key)
                If val IsNot Nothing AndAlso val.Type <> JTokenType.Null Then snapshot(f.Key) = val
            Next
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "
                    UPDATE dbo.SurveyInvites
                    SET first_name = @fn, last_name = @ln, company = @co, custom_id = @idn, custom_json = @cj,
                        updated_date = SYSUTCDATETIME()
                    WHERE id = @id AND status = 'pending'"
                Dim s = Function(x As String) If(CObj(x), DBNull.Value)
                cmd.Parameters.Add(New SqlParameter("@fn", SqlDbType.NVarChar, 100) With {.Value = s(v.FirstName)})
                cmd.Parameters.Add(New SqlParameter("@ln", SqlDbType.NVarChar, 100) With {.Value = s(v.LastName)})
                cmd.Parameters.Add(New SqlParameter("@co", SqlDbType.NVarChar, 200) With {.Value = s(v.Company)})
                cmd.Parameters.Add(New SqlParameter("@idn", SqlDbType.NVarChar, 100) With {.Value = s(v.IdNumber)})
                cmd.Parameters.Add(New SqlParameter("@cj", SqlDbType.NVarChar, -1) With {
                    .Value = If(snapshot.Count = 0, CObj(DBNull.Value), snapshot.ToString(Formatting.None))
                })
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = inv.Id})
                cmd.ExecuteNonQuery()
            End Using
        Next
    End Sub

    Public Sub AddSystemNote(conn As SqlConnection, tx As SqlTransaction, accountId As String, contactId As String,
                             note As String, userEmail As String)
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "
                INSERT INTO dbo.ContactNotes (id, account_id, contact_id, note_type, note, created_by)
                VALUES (LOWER(CONVERT(CHAR(36), NEWID())), @a, @c, 'system', @n, @u)"
            cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
            cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = contactId})
            cmd.Parameters.Add(New SqlParameter("@n", SqlDbType.NVarChar, -1) With {.Value = note})
            cmd.Parameters.Add(New SqlParameter("@u", SqlDbType.NVarChar, 320) With {.Value = If(CObj(userEmail), DBNull.Value)})
            cmd.ExecuteNonQuery()
        End Using
    End Sub

End Module
