Imports System.Data
Imports System.Net
Imports System.Security.Cryptography
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Invites. Two kinds of caller:
'   * Account users (logged in, invite's survey in their account) — full CRUD,
'     limited to their account.
'   * Respondents (TakeSurvey; anonymous, or logged in to another account) —
'     may only look an invite up by its token and advance its progress fields.
Public Class SurveyInviteController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.SurveyInvite)
    Private Shared ReadOnly ContactRepo As New JsonRepository(EntityRegistry.Contact)

    ' Alphabet for respondent passwords. Skips lookalikes (0/O, 1/I/L) so the
    ' code is readable when typed off a paper invite or printed report.
    Private Const PasswordAlphabet As String = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
    Private Const PasswordLength As Integer = 6

    ' Statuses a respondent may set while taking the survey.
    Private Shared ReadOnly RespondentStatuses As New HashSet(Of String)({"opened", "started", "completed"})

    ' Cryptographically-random short code. Uses RandomNumberGenerator so we
    ' don't lean on Random (which is seeded from the clock and can repeat
    ' under burst inserts). Six chars from a 31-char alphabet ≈ 30 bits of
    ' entropy — fine for a per-invite shared secret that's also gated by the
    ' invite token / respondent_id.
    Private Shared Function GeneratePassword() As String
        Dim sb As New System.Text.StringBuilder(PasswordLength)
        Using rng = RandomNumberGenerator.Create()
            Dim buf(PasswordLength - 1) As Byte
            rng.GetBytes(buf)
            For i = 0 To PasswordLength - 1
                sb.Append(PasswordAlphabet(buf(i) Mod PasswordAlphabet.Length))
            Next
        End Using
        Return sb.ToString()
    End Function

    ' 128-bit random URL token, used when the client doesn't supply one.
    Private Shared Function GenerateToken() As String
        Dim buf(15) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(buf)
        End Using
        Return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant()
    End Function

    ' Stamp a password and token onto the incoming invite payload if the
    ' caller didn't provide them. Treat null / missing / whitespace as
    ' "please generate".
    Private Shared Sub EnsureSecrets(data As JObject)
        If data Is Nothing Then Return
        If String.IsNullOrWhiteSpace(data.Value(Of String)("password")) Then data("password") = GeneratePassword()
        If String.IsNullOrWhiteSpace(data.Value(Of String)("token")) Then data("token") = GenerateToken()
    End Sub

    ' Invites must belong to a survey (and contact, if linked) in the
    ' caller's account.
    Private Function CheckSurvey(data As JObject) As IHttpActionResult
        Dim surveyId = data?.Value(Of String)("survey_id")
        If String.IsNullOrEmpty(surveyId) Then Return BadRequest("'survey_id' is required.")
        If Not Ownership.SurveyInCallerAccount(surveyId) Then Return NotFound()
        Return CheckContact(data)
    End Function

    Private Function CheckContact(data As JObject) As IHttpActionResult
        Dim contactId = ContactService.Clean(data?.Value(Of String)("contact_id"))
        If contactId IsNot Nothing AndAlso ContactRepo.GetById(contactId, CurrentUser.Scope()) Is Nothing Then
            Return BadRequest("Unknown contact.")
        End If
        Return Nothing
    End Function

    ' AllowAnonymous: TakeSurvey looks the invite up by token (an opaque
    ' secret) before letting the respondent in — the token IS the auth, so
    ' that lookup is unscoped and may filter on nothing else. Every other
    ' listing requires login and is limited to the caller's account.
    <HttpGet, Route("SurveyInvite"), AllowAnonymous>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As IHttpActionResult
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)

        Dim token = filterObj?.Value(Of String)("token")
        If Not String.IsNullOrEmpty(token) Then
            Return Ok(Repo.Find(New JObject From {{"token", token}}, "", 1, RepoScope.Unscoped))
        End If

        If CurrentUser.Info() Is Nothing Then Return Unauthorized()
        Return Ok(Repo.Find(filterObj, sort, limit, CurrentUser.Scope()))
    End Function

    <HttpGet, Route("SurveyInvite/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPost, Route("SurveyInvite")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        Dim bad = CheckSurvey(data)
        If bad IsNot Nothing Then Return bad
        EnsureSecrets(data)
        Return Ok(Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope()))
    End Function

    ' AllowAnonymous: TakeSurvey advances invite status (opened → started →
    ' completed) as the respondent moves through the survey. Account users
    ' editing their own invites get a full update; anyone else is treated as
    ' the respondent and may only set status/opened_at/completed_at.
    <HttpPut, Route("SurveyInvite/{id}"), AllowAnonymous>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")

        If CurrentUser.Info() IsNot Nothing AndAlso Repo.GetById(id, CurrentUser.Scope()) IsNot Nothing Then
            If data("survey_id") IsNot Nothing AndAlso Not Ownership.SurveyInCallerAccount(data.Value(Of String)("survey_id")) Then
                Return NotFound()
            End If
            Dim badContact = CheckContact(data)
            If badContact IsNot Nothing Then Return badContact
            Return Ok(Repo.Update(id, data, CurrentUser.Scope()))
        End If

        Dim respondentData = Ownership.Pick(data, "status", "opened_at", "completed_at")
        Dim status = respondentData.Value(Of String)("status")
        If status IsNot Nothing AndAlso Not RespondentStatuses.Contains(status) Then
            Return BadRequest($"Status '{status}' can't be set here.")
        End If
        If respondentData.Count = 0 Then Return BadRequest("Nothing to update.")

        Dim row = Repo.Update(id, respondentData, RepoScope.Unscoped)
        If row Is Nothing Then Return NotFound()
        Return Ok(New JObject From {{"id", row("id")}, {"status", row("status")}})
    End Function

    <HttpDelete, Route("SurveyInvite/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("SurveyInvite/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As IHttpActionResult
        If items Is Nothing Then Return BadRequest("Request body is required.")
        For Each item In items.OfType(Of JObject)()
            Dim bad = CheckSurvey(item)
            If bad IsNot Nothing Then Return bad
            EnsureSecrets(item)
        Next
        Return Ok(Repo.BulkCreate(items, CurrentUser.Email(), CurrentUser.Scope()))
    End Function

    ' Invite address-book contacts to a survey.
    '
    ' Body: { survey_id, group_ids?: [], contact_ids?: [], random_count?: N, dry_run?: bool }
    '   Recipients = active, non-hidden contacts in any of the groups plus any
    '   listed contacts. Skipped: contacts already invited to the survey (or
    '   whose email already has an ad-hoc invite) and suppressed emails.
    '   random_count picks N at random from what's left (legacy "random N not
    '   yet deployed"). Contacts without an email still get an invite (for
    '   deploy-without-email links).
    '
    ' Each invite snapshots the contact's name, company, ID number (as
    ' custom_id) and the custom fields that apply to this survey.
    ' Returns { created, already_invited, suppressed, eligible }.
    <HttpPost, Route("SurveyInvite/from-contacts")>
    Public Function FromContacts(<FromBody> data As JObject) As IHttpActionResult
        Dim surveyId = ContactService.Clean(data?.Value(Of String)("survey_id"))
        If surveyId Is Nothing Then Return BadRequest("'survey_id' is required.")
        If Not Ownership.SurveyInCallerAccount(surveyId) Then Return NotFound()
        Dim groupIds = If(ContactService.ReadIdList(data, "group_ids"), New List(Of String))
        Dim contactIds = If(ContactService.ReadIdList(data, "contact_ids"), New List(Of String))
        If groupIds.Count = 0 AndAlso contactIds.Count = 0 Then Return BadRequest("Pick at least one group or contact.")
        If groupIds.Count + contactIds.Count > 2000 Then Return BadRequest("Pick at most 2,000 contacts at a time; use a group for larger sends.")
        Dim randomCount = data.Value(Of Integer?)("random_count")
        If randomCount.HasValue AndAlso randomCount.Value < 1 Then Return BadRequest("'random_count' must be at least 1.")
        Dim dryRun = If(data.Value(Of Boolean?)("dry_run"), False)

        Dim accountId = CurrentUser.AccountId()
        Dim suppressed = ContactService.SuppressedEmails(accountId)
        Dim fields = ContactService.LoadFields(accountId).Values.
            Where(Function(f) f.SurveyIds Is Nothing OrElse f.SurveyIds.Contains(surveyId)).
            Select(Function(f) f.Key).ToList()

        ' Candidate contacts, with whether they're already invited.
        Dim candidates As New List(Of JObject)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                Dim gIn = If(groupIds.Count = 0, "NULL", String.Join(", ", groupIds.Select(Function(g, i) "@g" & i)))
                Dim cIn = If(contactIds.Count = 0, "NULL", String.Join(", ", contactIds.Select(Function(c, i) "@c" & i)))
                cmd.CommandText = $"
                    SELECT c.id, c.first_name, c.last_name, c.email, c.id_number, c.company, c.custom_json,
                           CASE WHEN EXISTS (SELECT 1 FROM dbo.SurveyInvites i
                                             WHERE i.survey_id = @s
                                               AND (i.contact_id = c.id OR (i.contact_id IS NULL AND c.email IS NOT NULL AND i.email = c.email)))
                                THEN 1 ELSE 0 END AS invited
                    FROM dbo.Contacts c
                    WHERE c.account_id = @a AND c.is_active = 1 AND c.is_hidden = 0
                      AND (c.id IN ({cIn})
                           OR EXISTS (SELECT 1 FROM dbo.ContactGroupMembers m
                                      JOIN dbo.ContactGroups g ON g.id = m.group_id AND g.account_id = @a AND g.is_active = 1
                                      WHERE m.contact_id = c.id AND m.group_id IN ({gIn})))"
                cmd.Parameters.Add(New SqlClient.SqlParameter("@s", SqlDbType.Char, 36) With {.Value = surveyId})
                cmd.Parameters.Add(New SqlClient.SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                For i = 0 To groupIds.Count - 1
                    cmd.Parameters.Add(New SqlClient.SqlParameter("@g" & i, SqlDbType.Char, 36) With {.Value = groupIds(i)})
                Next
                For i = 0 To contactIds.Count - 1
                    cmd.Parameters.Add(New SqlClient.SqlParameter("@c" & i, SqlDbType.Char, 36) With {.Value = contactIds(i)})
                Next
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        candidates.Add(New JObject From {
                            {"id", rdr.GetString(0)},
                            {"first_name", If(rdr.IsDBNull(1), Nothing, rdr.GetString(1))},
                            {"last_name", If(rdr.IsDBNull(2), Nothing, rdr.GetString(2))},
                            {"email", If(rdr.IsDBNull(3), Nothing, rdr.GetString(3))},
                            {"id_number", If(rdr.IsDBNull(4), Nothing, rdr.GetString(4))},
                            {"company", If(rdr.IsDBNull(5), Nothing, rdr.GetString(5))},
                            {"custom", If(rdr.IsDBNull(6), New JObject, JObject.Parse(rdr.GetString(6)))},
                            {"invited", rdr.GetInt32(7) = 1}
                        })
                    End While
                End Using
            End Using
        End Using

        Dim alreadyInvited = candidates.Where(Function(c) c.Value(Of Boolean)("invited")).ToList()
        Dim notInvited = candidates.Where(Function(c) Not c.Value(Of Boolean)("invited")).ToList()
        Dim suppressedList = notInvited.Where(Function(c) c.Value(Of String)("email") IsNot Nothing AndAlso suppressed.Contains(c.Value(Of String)("email"))).ToList()
        Dim eligible = notInvited.Except(suppressedList).ToList()

        Dim chosen = eligible
        If randomCount.HasValue AndAlso randomCount.Value < eligible.Count Then
            Dim rnd As New Random(RandomSeed())
            chosen = eligible.OrderBy(Function(x) rnd.Next()).Take(randomCount.Value).ToList()
        End If

        If Not dryRun AndAlso chosen.Count > 0 Then
            Using conn = Db.Open()
                Using tx = conn.BeginTransaction()
                    For Each c In chosen
                        Dim snapshot As New JObject
                        Dim custom = CType(c("custom"), JObject)
                        For Each k In fields
                            If custom(k) IsNot Nothing Then snapshot(k) = custom(k)
                        Next
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "
                                INSERT INTO dbo.SurveyInvites
                                    (id, survey_id, account_id, contact_id, email, token, [password], status,
                                     first_name, last_name, company, custom_id, custom_json, created_by)
                                VALUES
                                    (@id, @s, @a, @c, @em, @t, @pw, 'pending', @fn, @ln, @co, @cid, @cj, @u)"
                            Dim s = Function(x As String) If(CObj(x), DBNull.Value)
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@id", SqlDbType.Char, 36) With {.Value = Guid.NewGuid().ToString()})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@s", SqlDbType.Char, 36) With {.Value = surveyId})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@c", SqlDbType.Char, 36) With {.Value = c.Value(Of String)("id")})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@em", SqlDbType.NVarChar, 320) With {.Value = s(c.Value(Of String)("email"))})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@t", SqlDbType.NVarChar, 100) With {.Value = GenerateToken()})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@pw", SqlDbType.NVarChar, 20) With {.Value = GeneratePassword()})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@fn", SqlDbType.NVarChar, 100) With {.Value = s(c.Value(Of String)("first_name"))})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@ln", SqlDbType.NVarChar, 100) With {.Value = s(c.Value(Of String)("last_name"))})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@co", SqlDbType.NVarChar, 200) With {.Value = s(c.Value(Of String)("company"))})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@cid", SqlDbType.NVarChar, 100) With {.Value = s(c.Value(Of String)("id_number"))})
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@cj", SqlDbType.NVarChar, -1) With {
                                .Value = If(snapshot.Count = 0, CObj(DBNull.Value), snapshot.ToString(Newtonsoft.Json.Formatting.None))
                            })
                            cmd.Parameters.Add(New SqlClient.SqlParameter("@u", SqlDbType.NVarChar, 320) With {.Value = CurrentUser.Email()})
                            cmd.ExecuteNonQuery()
                        End Using
                    Next
                    tx.Commit()
                End Using
            End Using
        End If

        Return Ok(New JObject From {
            {"dry_run", dryRun},
            {"created", If(dryRun, 0, chosen.Count)},
            {"would_create", chosen.Count},
            {"eligible", eligible.Count},
            {"already_invited", alreadyInvited.Count},
            {"suppressed", suppressedList.Count}
        })
    End Function

    Private Shared Function RandomSeed() As Integer
        Dim buf(3) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(buf)
        End Using
        Return BitConverter.ToInt32(buf, 0)
    End Function
End Class
