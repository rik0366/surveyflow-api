Imports System.Configuration
Imports System.Data
Imports System.Data.SqlClient
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Server-side "functions" — equivalents of base44.functions.invoke(name, args).
'
' Each function is its own POST endpoint under /api/functions/<name> so the
' adapter on the React side can keep the SDK call signature unchanged.
'
' Currently implemented:
'   sendSurveyEmail          — body: { to, subject, html }
'   getQuestionStats         — body: { survey_id, question_id, question_type }
'   sendScheduledReminders   — body: { survey_id, subject_template?, body_template? }
'
' Deferred to phase 2:
'   generateFollowUpQuestion   (AI; out of scope until phase 2)
'
' Note: sendScheduledReminders is its own endpoint planned for the cron
' job, not wired here yet.
Public Class FunctionsController
    Inherits ApiController


    ' --- getQuestionStats -------------------------------------------------
    ' Aggregates completed responses for a single question and returns stats
    ' the take-survey "Show feedback after answer" panel uses.
    '
    ' Response shape matches what QuestionFeedback.jsx expects:
    '   { data: { stats: {...type-specific...}, total: N } }
    '
    ' "stats" payload by question_type:
    '   rating            { average, top_response, top_response_count }
    '   multiple_choice   { top_response, top_response_pct, breakdown:[{value,pct,count}] }
    '   dropdown          (same as multiple_choice)
    '   checkbox          (same — answers are arrays, each element counted once per response)
    '   likert            { row_averages:[{row, average}] }
    '   rank_order        { top_response, top_response_count }
    '
    ' Anonymous: open endpoint (the take-survey page calls it without auth).
    <HttpPost, Route("functions/getQuestionStats"), AllowAnonymous>
    Public Function GetQuestionStats(<FromBody> req As JObject) As IHttpActionResult
        If req Is Nothing Then Return BadRequest("Request body is required.")

        Dim surveyId    = req.Value(Of String)("survey_id")
        Dim questionId  = req.Value(Of String)("question_id")
        Dim questionType = req.Value(Of String)("question_type")

        If String.IsNullOrEmpty(surveyId) Then Return BadRequest("'survey_id' is required.")
        If String.IsNullOrEmpty(questionId) Then Return BadRequest("'question_id' is required.")

        ' Pull the answer for our question out of every completed response.
        Dim answers As New List(Of JToken)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT answers_json
                    FROM dbo.SurveyResponses
                    WHERE survey_id = @sid AND status = 'completed' AND answers_json IS NOT NULL"
                cmd.Parameters.Add(New SqlParameter("@sid", SqlDbType.Char, 36) With {.Value = surveyId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        Try
                            Dim ans = JObject.Parse(rdr.GetString(0))
                            Dim val = ans(questionId)
                            If val IsNot Nothing AndAlso val.Type <> JTokenType.Null Then
                                answers.Add(val)
                            End If
                        Catch
                            ' Skip rows with malformed JSON; CHECK constraint should prevent this.
                        End Try
                    End While
                End Using
            End Using
        End Using

        Dim total = answers.Count
        Dim stats = ComputeStats(questionType, answers)

        Return Ok(New JObject From {
            {"data", New JObject From {
                {"stats", stats},
                {"total", total}
            }}
        })
    End Function


    Private Shared Function ComputeStats(questionType As String, answers As List(Of JToken)) As JObject
        If answers.Count = 0 Then Return New JObject

        Select Case questionType
            Case "rating", "nps"
                Return RatingStats(answers)
            Case "multiple_choice", "dropdown", "image_choice", "emoji"
                Return ChoiceStats(answers, multiSelect:=False)
            Case "checkbox"
                Return ChoiceStats(answers, multiSelect:=True)
            Case "likert"
                Return LikertStats(answers)
            Case "rank_order"
                Return RankStats(answers)
            Case Else
                Return New JObject
        End Select
    End Function


    Private Shared Function RatingStats(answers As List(Of JToken)) As JObject
        Dim nums = answers.
            Select(Function(a) AsDouble(a)).
            Where(Function(d) d.HasValue).
            Select(Function(d) d.Value).
            ToList()
        If nums.Count = 0 Then Return New JObject

        Dim avg = Math.Round(nums.Average(), 2)
        Dim mode = nums.GroupBy(Function(n) n).
            OrderByDescending(Function(g) g.Count()).
            First()

        Return New JObject From {
            {"average", avg},
            {"top_response", mode.Key},
            {"top_response_count", mode.Count()}
        }
    End Function


    Private Shared Function ChoiceStats(answers As List(Of JToken), multiSelect As Boolean) As JObject
        Dim counts As New Dictionary(Of String, Integer)
        Dim totalResponses = 0

        For Each a In answers
            totalResponses += 1
            If multiSelect AndAlso TypeOf a Is JArray Then
                For Each v In CType(a, JArray)
                    BumpCount(counts, v.ToString())
                Next
            Else
                BumpCount(counts, a.ToString())
            End If
        Next

        If counts.Count = 0 Then Return New JObject

        Dim totalCounts = counts.Values.Sum()
        Dim breakdown = counts.
            OrderByDescending(Function(kv) kv.Value).
            Select(Function(kv) New JObject From {
                {"value", kv.Key},
                {"count", kv.Value},
                {"pct", Math.Round(100.0 * kv.Value / totalCounts, 1)}
            }).
            ToList()

        Dim top = breakdown(0)
        Return New JObject From {
            {"top_response", top("value")},
            {"top_response_count", top("count")},
            {"top_response_pct", top("pct")},
            {"breakdown", New JArray(breakdown)}
        }
    End Function


    Private Shared Function LikertStats(answers As List(Of JToken)) As JObject
        ' Likert answers are objects keyed by row name → numeric value.
        Dim rowSums As New Dictionary(Of String, Double)
        Dim rowCounts As New Dictionary(Of String, Integer)

        For Each a In answers
            If TypeOf a Is JObject Then
                For Each prop In CType(a, JObject).Properties()
                    Dim v = AsDouble(prop.Value)
                    If v.HasValue Then
                        If Not rowSums.ContainsKey(prop.Name) Then
                            rowSums(prop.Name) = 0
                            rowCounts(prop.Name) = 0
                        End If
                        rowSums(prop.Name) += v.Value
                        rowCounts(prop.Name) += 1
                    End If
                Next
            End If
        Next

        Dim rowAverages As New JArray
        For Each row In rowSums.Keys
            rowAverages.Add(New JObject From {
                {"row", row},
                {"average", Math.Round(rowSums(row) / rowCounts(row), 2)}
            })
        Next

        Return New JObject From {{"row_averages", rowAverages}}
    End Function


    Private Shared Function RankStats(answers As List(Of JToken)) As JObject
        ' rank_order answers are arrays — index 0 is the respondent's #1 pick.
        Dim firstPickCounts As New Dictionary(Of String, Integer)
        For Each a In answers
            If TypeOf a Is JArray Then
                Dim arr = CType(a, JArray)
                If arr.Count > 0 Then BumpCount(firstPickCounts, arr(0).ToString())
            End If
        Next
        If firstPickCounts.Count = 0 Then Return New JObject

        Dim top = firstPickCounts.OrderByDescending(Function(kv) kv.Value).First()
        Return New JObject From {
            {"top_response", top.Key},
            {"top_response_count", top.Value}
        }
    End Function


    Private Shared Sub BumpCount(d As Dictionary(Of String, Integer), key As String)
        If String.IsNullOrEmpty(key) Then Return
        If d.ContainsKey(key) Then d(key) += 1 Else d(key) = 1
    End Sub

    Private Shared Function AsDouble(t As JToken) As Double?
        If t Is Nothing OrElse t.Type = JTokenType.Null Then Return Nothing
        Try
            Return CDbl(t)
        Catch
            Dim s = t.ToString()
            Dim parsed As Double
            If Double.TryParse(s, parsed) Then Return parsed
            Return Nothing
        End Try
    End Function


    ' --- sendScheduledReminders -------------------------------------------
    ' Sends a reminder email to every invitee who was sent the original
    ' invite but hasn't completed the survey yet.
    '
    ' Body shape (from SurveyDistribute.jsx):
    '   { survey_id, subject_template?, body_template? }
    '
    ' Both templates support {{survey_title}}, {{recipient_email}},
    ' {{survey_link}} placeholders. The "pending" status (never sent) is
    ' skipped — reminders only go to people who already received an invite.
    <HttpPost, Route("functions/sendScheduledReminders"), Authorize>
    Public Async Function SendScheduledReminders(<FromBody> req As JObject) As Task(Of IHttpActionResult)
        If req Is Nothing Then Return BadRequest("Request body is required.")
        Dim surveyId = req.Value(Of String)("survey_id")
        If String.IsNullOrEmpty(surveyId) Then Return BadRequest("'survey_id' is required.")

        Dim subjectTemplate = If(req.Value(Of String)("subject_template"), "Reminder: {{survey_title}}")
        Dim bodyTemplate = If(req.Value(Of String)("body_template"), DefaultReminderBody())

        ' Fetch the survey (title, status) — only from the caller's account.
        Dim accountId = CurrentUser.AccountId()
        Dim surveyTitle As String = Nothing
        Dim surveyStatus As String = Nothing
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT title, status FROM dbo.Surveys WHERE id = @id AND account_id = @acct"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = surveyId})
                cmd.Parameters.Add(New SqlParameter("@acct", SqlDbType.Char, 36) With {.Value = accountId})
                Using rdr = cmd.ExecuteReader()
                    If rdr.Read() Then
                        surveyTitle  = If(rdr.IsDBNull(0), "", rdr.GetString(0))
                        surveyStatus = If(rdr.IsDBNull(1), "", rdr.GetString(1))
                    End If
                End Using
            End Using
        End Using

        If surveyTitle Is Nothing Then Return NotFound()
        If surveyStatus <> "active" Then
            Return Ok(New JObject From {
                {"skipped", True},
                {"reason", "Survey is not active"}
            })
        End If

        ' Fetch eligible invites (sent / opened / started — not pending, not completed).
        Dim invites As New List(Of (id As String, email As String, token As String))
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT id, email, token
                    FROM dbo.SurveyInvites
                    WHERE survey_id = @sid
                      AND status NOT IN ('pending', 'completed')
                      AND email IS NOT NULL"
                cmd.Parameters.Add(New SqlParameter("@sid", SqlDbType.Char, 36) With {.Value = surveyId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        invites.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)))
                    End While
                End Using
            End Using
        End Using

        If invites.Count = 0 Then
            Return Ok(New JObject From {
                {"success", True},
                {"sent", 0},
                {"total", 0},
                {"message", "No eligible recipients"}
            })
        End If

        Dim baseUrl = If(ConfigurationManager.AppSettings("App:PublicBaseUrl"), "").TrimEnd("/"c)
        Dim sent = 0
        Dim skipped = 0
        Dim errors As New JArray

        For Each invite In invites
            ' Skip recipients who've opted out of this account's emails or
            ' are globally suppressed. Tracked separately from `errors` so
            ' the response counts are accurate (a skipped email is neither
            ' sent nor failed).
            If Ownership.IsSuppressed(accountId, invite.email) Then
                skipped += 1
                Continue For
            End If

            Dim surveyUrl = $"{baseUrl}/take?sid={surveyId}&t={invite.token}"
            Dim subject = ResolveTemplate(subjectTemplate, surveyTitle, invite.email, surveyUrl)
            Dim bodyText = ResolveTemplate(bodyTemplate, surveyTitle, invite.email, surveyUrl)
            Dim html = BuildReminderHtml(bodyText, surveyUrl)

            Try
                Await ResendEmailService.SendAsync(invite.email, subject, html)
                sent += 1
            Catch ex As Exception
                errors.Add(New JObject From {
                    {"email", invite.email},
                    {"error", ex.Message}
                })
            End Try
        Next

        Return Ok(New JObject From {
            {"success", True},
            {"sent", sent},
            {"skipped", skipped},
            {"total", invites.Count},
            {"errors", errors}
        })
    End Function


    Private Shared Function DefaultReminderBody() As String
        Return "Hi," & vbLf & vbLf &
               "This is a friendly reminder that you haven't yet completed the survey: {{survey_title}}." & vbLf & vbLf &
               "Please take a few minutes to share your feedback:" & vbLf &
               "{{survey_link}}" & vbLf & vbLf &
               "Thank you!"
    End Function


    Private Shared Function ResolveTemplate(template As String, title As String, email As String, link As String) As String
        If String.IsNullOrEmpty(template) Then Return ""
        Return template.
            Replace("{{survey_title}}",     If(title, "Survey")).
            Replace("{{recipient_email}}",  If(email, "")).
            Replace("{{survey_link}}",      link)
    End Function


    Private Shared Function BuildReminderHtml(bodyText As String, surveyUrl As String) As String
        ' HTML-escape the body, then linkify the survey URL token into a button.
        Dim escaped = System.Net.WebUtility.HtmlEncode(If(bodyText, ""))
        Dim escapedUrl = Regex.Escape(surveyUrl)
        Dim button = $"<a href=""{System.Net.WebUtility.HtmlEncode(surveyUrl)}"" style=""display:inline-block;background:#1AA9D4;color:#fff;padding:12px 28px;border-radius:8px;text-decoration:none;font-weight:600;"">{System.Net.WebUtility.HtmlEncode(surveyUrl)}</a>"
        Dim withButton = Regex.Replace(escaped, escapedUrl, button)

        Return $"<div style=""font-family:-apple-system,sans-serif;max-width:520px;margin:0 auto;padding:32px;color:#1a1a2e;""><pre style=""font-family:inherit;white-space:pre-wrap;margin:0;"">{withButton}</pre></div>"
    End Function


    <HttpPost, Route("functions/sendSurveyEmail"), Authorize>
    Public Async Function SendSurveyEmail(<FromBody> req As JObject) As Task(Of IHttpActionResult)
        If req Is Nothing Then Return BadRequest("Request body is required.")

        Dim toAddress = req.Value(Of String)("to")
        Dim subject   = req.Value(Of String)("subject")
        Dim html      = req.Value(Of String)("html")

        If String.IsNullOrEmpty(toAddress) Then Return BadRequest("'to' is required.")
        If String.IsNullOrEmpty(subject) Then Return BadRequest("'subject' is required.")
        If String.IsNullOrEmpty(html) Then Return BadRequest("'html' is required.")

        ' Suppression check — silently skip recipients who've opted out of
        ' this account's emails or are globally suppressed. Returns 200 with
        ' status="skipped" so the caller can count it as not-failed but also
        ' not-sent.
        If Ownership.IsSuppressed(CurrentUser.AccountId(), toAddress) Then
            Return Ok(New JObject From {
                {"status", "skipped"},
                {"reason", "blacklisted"},
                {"to", toAddress}
            })
        End If

        Try
            Dim respBody = Await ResendEmailService.SendAsync(toAddress, subject, html)
            Return Ok(JObject.Parse(respBody))
        Catch ex As HttpRequestException
            ' Bubble Resend's error message so the client can see what went wrong.
            Dim errBody As New JObject From {
                {"status", "error"},
                {"source", "resend"},
                {"error", ex.Message}
            }
            Return Content(HttpStatusCode.BadGateway, errBody)
        End Try
    End Function

End Class
