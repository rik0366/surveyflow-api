Imports System.Configuration
Imports System.Data
Imports System.Data.SqlClient
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Threading.Tasks
Imports System.Web.Http
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' Background job endpoints. Authenticated by a shared secret in the
' X-Job-Key header (configured in secrets.config as Jobs:ApiKey) so a
' Windows Scheduled Task or SQL Agent job can call them without a user
' session.
'
' Currently:
'   POST /api/jobs/processSentiment
'     body: { survey_id?: string, max_responses?: number }
'     finds completed SurveyResponses with sentiment_score IS NULL,
'     calls OpenAI per response, writes sentiment_score + sentiment_label,
'     returns { processed, skipped, errors }.
Public Class JobsController
    Inherits ApiController

    Private Shared ReadOnly OpenAiClient As New HttpClient() With {
        .Timeout = TimeSpan.FromMinutes(2)
    }

    Private Shared ReadOnly SurveyRepo As New JsonRepository(EntityRegistry.Survey)


    <HttpPost, Route("jobs/processSentiment"), AllowAnonymous>
    Public Async Function ProcessSentiment(<FromBody> req As JObject) As Task(Of IHttpActionResult)
        ' Auth: either a matching X-Job-Key (cron path) or an authenticated
        ' user (admin clicking "Run sentiment now" in the UI).
        Dim expected = ConfigurationManager.AppSettings("Jobs:ApiKey")
        Dim providedHeader As IEnumerable(Of String) = Nothing
        Dim provided = ""
        If Request.Headers.TryGetValues("X-Job-Key", providedHeader) Then
            provided = providedHeader.FirstOrDefault()
        End If
        Dim keyOk = (Not String.IsNullOrEmpty(expected)) AndAlso (provided = expected)
        Dim authed = (System.Web.HttpContext.Current?.User?.Identity?.IsAuthenticated).GetValueOrDefault(False)
        If Not keyOk AndAlso Not authed Then Return Unauthorized()

        Dim surveyId As String = Nothing
        Dim maxResponses As Integer = 50
        If req IsNot Nothing Then
            surveyId = req.Value(Of String)("survey_id")
            Dim n = req.Value(Of Integer?)("max_responses")
            If n.HasValue AndAlso n.Value > 0 Then maxResponses = Math.Min(n.Value, 500)
        End If

        ' Pull candidate responses (completed, no sentiment yet)
        Dim candidates As New List(Of SentimentCandidate)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                Dim sql = New StringBuilder()
                sql.Append("SELECT TOP (@n) id, survey_id, answers_json FROM dbo.SurveyResponses ")
                sql.Append("WHERE status = 'completed' AND sentiment_score IS NULL AND answers_json IS NOT NULL")
                If Not String.IsNullOrEmpty(surveyId) Then
                    sql.Append(" AND survey_id = @survey_id")
                End If
                cmd.CommandText = sql.ToString()
                cmd.Parameters.Add(New SqlParameter("@n", SqlDbType.Int) With {.Value = maxResponses})
                If Not String.IsNullOrEmpty(surveyId) Then
                    cmd.Parameters.Add(New SqlParameter("@survey_id", SqlDbType.Char, 36) With {.Value = surveyId})
                End If
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        Try
                            candidates.Add(New SentimentCandidate With {
                                .Id = rdr.GetString(0),
                                .SurveyId = rdr.GetString(1),
                                .Answers = JObject.Parse(rdr.GetString(2))
                            })
                        Catch
                            ' Skip rows with malformed answers JSON.
                        End Try
                    End While
                End Using
            End Using
        End Using

        Dim processed = 0
        Dim skipped = 0
        Dim errors As New JArray

        ' Cache surveys we look up so we don't fetch the same one repeatedly
        ' when many responses share a survey.
        Dim surveyCache As New Dictionary(Of String, JObject)

        For Each c In candidates
            Try
                Dim survey As JObject = Nothing
                If Not surveyCache.TryGetValue(c.SurveyId, survey) Then
                    survey = SurveyRepo.GetById(c.SurveyId)
                    surveyCache(c.SurveyId) = survey
                End If

                If survey Is Nothing Then
                    skipped += 1
                    Continue For
                End If

                Dim aiEnabled = False
                Dim settingsTok = survey("settings")
                If settingsTok IsNot Nothing AndAlso settingsTok.Type = JTokenType.Object Then
                    aiEnabled = CType(settingsTok, JObject).Value(Of Boolean?)("ai_enabled").GetValueOrDefault(False)
                End If
                If Not aiEnabled Then
                    skipped += 1
                    Continue For
                End If

                Dim textBlock = ExtractTextAnswers(survey, c.Answers)
                If String.IsNullOrWhiteSpace(textBlock) Then
                    skipped += 1
                    Continue For
                End If

                Dim ownerEmail = survey.Value(Of String)("created_by")
                Dim sentiment = Await AnalyzeSentiment(textBlock, ownerEmail, c.SurveyId).ConfigureAwait(False)
                If sentiment Is Nothing Then
                    errors.Add(New JObject From {
                        {"id", c.Id},
                        {"error", "OpenAI returned no sentiment"}
                    })
                    Continue For
                End If

                Using conn = Db.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            UPDATE dbo.SurveyResponses
                            SET sentiment_score = @s,
                                sentiment_label = @l,
                                updated_date = SYSUTCDATETIME()
                            WHERE id = @id"
                        cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char,     36) With {.Value = c.Id})
                        cmd.Parameters.Add(New SqlParameter("@s",  SqlDbType.Float)        With {.Value = sentiment("score").Value(Of Double)()})
                        cmd.Parameters.Add(New SqlParameter("@l",  SqlDbType.NVarChar, 50) With {.Value = sentiment("label").ToString()})
                        cmd.ExecuteNonQuery()
                    End Using
                End Using

                processed += 1
            Catch ex As Exception
                errors.Add(New JObject From {
                    {"id", c.Id},
                    {"error", ex.Message}
                })
            End Try
        Next

        Return Ok(New JObject From {
            {"status", "ok"},
            {"candidates", candidates.Count},
            {"processed", processed},
            {"skipped", skipped},
            {"errors", errors}
        })
    End Function


    Private Shared Function ExtractTextAnswers(survey As JObject, answers As JObject) As String
        Dim sb As New StringBuilder()
        Dim questions = TryCast(survey("questions"), JArray)
        If questions Is Nothing Then Return ""

        For Each qTok As JToken In questions
            If qTok.Type <> JTokenType.Object Then Continue For
            Dim q = CType(qTok, JObject)
            Dim qType = q.Value(Of String)("type")
            If qType <> "short_text" AndAlso qType <> "long_text" Then Continue For

            Dim qid = q.Value(Of String)("id")
            Dim ans = answers(qid)
            If ans Is Nothing OrElse ans.Type = JTokenType.Null Then Continue For

            Dim text = ans.ToString().Trim()
            If text.Length = 0 Then Continue For

            sb.AppendLine("Q: " & q.Value(Of String)("text"))
            sb.AppendLine("A: " & text)
            sb.AppendLine()
        Next

        Return sb.ToString()
    End Function


    Private Shared Async Function AnalyzeSentiment(textBlock As String, attribEmail As String, surveyId As String) As Task(Of JObject)
        Dim apiKey = ConfigurationManager.AppSettings("Ai:OpenAIKey")
        If String.IsNullOrEmpty(apiKey) Then Return Nothing

        Dim model = If(ConfigurationManager.AppSettings("Ai:Model"), "gpt-4o-mini")

        Dim prompt = $"Analyze the overall sentiment expressed in the following survey responses from one respondent. Return:
- score: a number from -1.0 (very negative) to 1.0 (very positive)
- label: one of ""positive"", ""neutral"", or ""negative""

Use these rough thresholds: score >= 0.3 → positive, score <= -0.3 → negative, otherwise neutral.

Respondent's answers:

{textBlock}"

        Dim schema As New JObject From {
            {"type", "object"},
            {"additionalProperties", False},
            {"required", New JArray() From {"score", "label"}},
            {"properties", New JObject From {
                {"score", New JObject From {{"type", "number"}}},
                {"label", New JObject From {
                    {"type", "string"},
                    {"enum", New JArray() From {"positive", "neutral", "negative"}}
                }}
            }}
        }

        Dim body As New JObject From {
            {"model", model},
            {"messages", New JArray From {
                New JObject From {{"role", "user"}, {"content", prompt}}
            }},
            {"response_format", New JObject From {
                {"type", "json_schema"},
                {"json_schema", New JObject From {
                    {"name", "sentiment"},
                    {"strict", True},
                    {"schema", schema}
                }}
            }}
        }

        Using rq As New HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            rq.Headers.Authorization = New AuthenticationHeaderValue("Bearer", apiKey)
            rq.Content = New StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")

            Using rs = Await OpenAiClient.SendAsync(rq).ConfigureAwait(False)
                Dim respText = Await rs.Content.ReadAsStringAsync().ConfigureAwait(False)
                If Not rs.IsSuccessStatusCode Then Return Nothing

                Dim parsed = JObject.Parse(respText)
                Dim msgContent = parsed.SelectToken("choices[0].message.content")?.ToString()
                If String.IsNullOrEmpty(msgContent) Then Return Nothing

                ' Log this call for billing/reporting.
                Dim usage = parsed("usage")
                AiUsageLogger.Log(
                    attribEmail, surveyId, "sentiment_batch", model,
                    usage?.Value(Of Integer?)("prompt_tokens"),
                    usage?.Value(Of Integer?)("completion_tokens"),
                    usage?.Value(Of Integer?)("total_tokens"),
                    True)

                Try
                    Return JObject.Parse(msgContent)
                Catch
                    Return Nothing
                End Try
            End Using
        End Using
    End Function


    Private Class SentimentCandidate
        Public Property Id As String
        Public Property SurveyId As String
        Public Property Answers As JObject
    End Class

End Class
