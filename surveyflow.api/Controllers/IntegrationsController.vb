Imports System.Configuration
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Threading.Tasks
Imports System.Web.Hosting
Imports System.Web.Http
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' base44.integrations.Core.* equivalents.
'
' UploadFile: replaces base44.integrations.Core.UploadFile({file}). Takes a
' multipart/form-data POST with a single file field, stores it under
' ~/uploads/<guid><ext> on the API server, and returns { file_url }.
' The URL is /api/uploads/<guid><ext> — IIS strips "/api" when routing to
' the sub-app, so the file is served as static content from the API folder.
'
' Phase 2: add S3/Azure Blob backend for HA, plus respondent-side upload
' for file_upload question types (currently only admins upload).
Public Class IntegrationsController
    Inherits ApiController

    Private Shared ReadOnly AllowedExtensions As String() = {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt"
    }

    Private Shared ReadOnly OpenAiClient As New HttpClient() With {
        .Timeout = TimeSpan.FromMinutes(2)
    }

    Private Shared ReadOnly SurveyRepo As New JsonRepository(EntityRegistry.Survey)

    ' --- InvokeLLM ---------------------------------------------------------
    ' Replaces base44.integrations.Core.InvokeLLM. Calls OpenAI's Chat
    ' Completions API with structured outputs (JSON schema).
    '
    ' Body shape:
    '   { prompt, model?, response_json_schema?, survey_id? }
    '
    ' If survey_id is provided, the survey's settings.ai_enabled flag must be
    ' true — otherwise we refuse the call without spending tokens. Calls
    ' without a survey_id (e.g. admin-only "save question to library" quality
    ' analysis) are allowed for any logged-in admin.
    '
    ' The "model" arg accepts the Base44 alias "gpt_5" (mapped to the
    ' configured default) or any real OpenAI model name to pass through.
    <HttpPost, Route("integrations/InvokeLLM"), AllowAnonymous>
    Public Async Function InvokeLLM(<FromBody> req As JObject) As Task(Of IHttpActionResult)
        If req Is Nothing Then Return BadRequest("Request body is required.")

        Dim prompt = req.Value(Of String)("prompt")
        If String.IsNullOrEmpty(prompt) Then Return BadRequest("'prompt' is required.")

        Dim modelAlias = req.Value(Of String)("model")
        Dim schema = req("response_json_schema")
        Dim surveyId = req.Value(Of String)("survey_id")

        ' Authorization: either a survey_id whose survey has ai_enabled=true
        ' (TakeSurvey follow-up case — respondent is anonymous), OR a
        ' logged-in admin (TextAnalyticsReport, SaveToLibrary).
        If Not String.IsNullOrEmpty(surveyId) Then
            If Not IsAiEnabledForSurvey(surveyId) Then
                Return Content(HttpStatusCode.Forbidden, New JObject From {
                    {"status", "error"},
                    {"reason", "ai_disabled"},
                    {"message", "AI features are not enabled for this survey."}
                })
            End If
        Else
            Dim isAuthed = (System.Web.HttpContext.Current?.User?.Identity?.IsAuthenticated).GetValueOrDefault(False)
            If Not isAuthed Then Return Unauthorized()
        End If

        Dim apiKey = ConfigurationManager.AppSettings("Ai:OpenAIKey")
        If String.IsNullOrWhiteSpace(apiKey) Then
            Return Content(HttpStatusCode.InternalServerError, New JObject From {
                {"status", "error"},
                {"reason", "openai_not_configured"},
                {"message", "Ai:OpenAIKey is not set in secrets.config."}
            })
        End If

        Dim model = ResolveModel(modelAlias)

        Dim openAiBody As New JObject From {
            {"model", model},
            {"messages", New JArray From {
                New JObject From {
                    {"role", "user"},
                    {"content", prompt}
                }
            }}
        }

        If schema IsNot Nothing AndAlso schema.Type = JTokenType.Object Then
            ' OpenAI's strict structured-output mode requires every object to
            ' declare additionalProperties:false and list every property in
            ' "required". The schemas in the React code don't follow this, so
            ' we normalise server-side rather than refactor every call site.
            Dim normalised = NormalizeSchemaForStrictMode(schema.DeepClone())
            openAiBody("response_format") = New JObject From {
                {"type", "json_schema"},
                {"json_schema", New JObject From {
                    {"name", "response"},
                    {"strict", True},
                    {"schema", normalised}
                }}
            }
        End If

        Try
            Using openAiReq As New HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
                openAiReq.Headers.Authorization = New AuthenticationHeaderValue("Bearer", apiKey)
                openAiReq.Content = New StringContent(openAiBody.ToString(Formatting.None), Encoding.UTF8, "application/json")

                Using resp = Await OpenAiClient.SendAsync(openAiReq).ConfigureAwait(False)
                    Dim respBody = Await resp.Content.ReadAsStringAsync().ConfigureAwait(False)

                    If Not resp.IsSuccessStatusCode Then
                        Return Content(HttpStatusCode.BadGateway, New JObject From {
                            {"status", "error"},
                            {"source", "openai"},
                            {"http_status", CInt(resp.StatusCode)},
                            {"body", respBody}
                        })
                    End If

                    Dim parsed = JObject.Parse(respBody)
                    Dim messageContent = If(parsed.SelectToken("choices[0].message.content")?.ToString(), "")

                    If String.IsNullOrEmpty(messageContent) Then
                        Return Content(HttpStatusCode.BadGateway, New JObject From {
                            {"status", "error"},
                            {"source", "openai"},
                            {"message", "Empty response from OpenAI."}
                        })
                    End If

                    ' Log usage for billing. Attribution: prefer the logged-in
                    ' user; otherwise fall back to the survey owner (anonymous
                    ' respondent triggering an AI follow-up, for example).
                    Dim attribEmail = If(System.Web.HttpContext.Current?.User?.Identity?.Name, "")
                    If String.IsNullOrEmpty(attribEmail) AndAlso Not String.IsNullOrEmpty(surveyId) Then
                        Try
                            Dim s = SurveyRepo.GetById(surveyId, RepoScope.Unscoped)
                            attribEmail = If(s?.Value(Of String)("created_by"), "")
                        Catch
                        End Try
                    End If
                    Dim usage = parsed("usage")
                    AiUsageLogger.Log(
                        attribEmail, surveyId, "InvokeLLM", model,
                        usage?.Value(Of Integer?)("prompt_tokens"),
                        usage?.Value(Of Integer?)("completion_tokens"),
                        usage?.Value(Of Integer?)("total_tokens"),
                        True)

                    Try
                        Dim result = JToken.Parse(messageContent)
                        Return Ok(result)
                    Catch ex As Exception
                        Return Content(HttpStatusCode.BadGateway, New JObject From {
                            {"status", "error"},
                            {"source", "openai"},
                            {"message", "OpenAI returned non-JSON: " & ex.Message},
                            {"raw", messageContent}
                        })
                    End Try
                End Using
            End Using
        Catch ex As Exception
            Return Content(HttpStatusCode.BadGateway, New JObject From {
                {"status", "error"},
                {"source", "openai"},
                {"message", ex.Message}
            })
        End Try
    End Function


    Private Shared Function ResolveModel(modelAlias As String) As String
        Dim defaultModel = If(ConfigurationManager.AppSettings("Ai:Model"), "gpt-4o-mini")
        If String.IsNullOrEmpty(modelAlias) Then Return defaultModel
        ' Map known Base44 aliases to the configured default; pass everything
        ' else through (callers can request a specific real model name).
        Select Case modelAlias.ToLowerInvariant()
            Case "gpt_5", "gpt-5", "gpt5"
                Return defaultModel
            Case Else
                Return modelAlias
        End Select
    End Function


    Private Shared Function IsAiEnabledForSurvey(surveyId As String) As Boolean
        Try
            Dim survey = SurveyRepo.GetById(surveyId, RepoScope.Unscoped)
            If survey Is Nothing Then Return False
            Dim settings = survey("settings")
            If settings Is Nothing OrElse settings.Type <> JTokenType.Object Then Return False
            Return CType(settings, JObject).Value(Of Boolean?)("ai_enabled").GetValueOrDefault(False)
        Catch
            Return False
        End Try
    End Function


    ' Walk the schema and ensure every object has additionalProperties:false
    ' and required = [all property names], as required by OpenAI strict mode.
    Private Shared Function NormalizeSchemaForStrictMode(node As JToken) As JToken
        If node Is Nothing OrElse node.Type <> JTokenType.Object Then Return node
        Dim obj = CType(node, JObject)
        Dim type = obj.Value(Of String)("type")

        If type = "object" Then
            Dim propsToken = obj("properties")
            If propsToken IsNot Nothing AndAlso propsToken.Type = JTokenType.Object Then
                Dim propsObj = CType(propsToken, JObject)
                obj("additionalProperties") = False
                Dim names As New JArray
                For Each prop In propsObj.Properties().ToList()
                    names.Add(prop.Name)
                    propsObj(prop.Name) = NormalizeSchemaForStrictMode(prop.Value)
                Next
                obj("required") = names
            End If
        ElseIf type = "array" Then
            Dim items = obj("items")
            If items IsNot Nothing Then
                obj("items") = NormalizeSchemaForStrictMode(items)
            End If
        End If

        Return obj
    End Function


    <HttpPost, Route("integrations/UploadFile"), Authorize>
    Public Async Function UploadFile() As Task(Of IHttpActionResult)
        If Not Request.Content.IsMimeMultipartContent() Then
            Return BadRequest("multipart/form-data required")
        End If

        Dim provider As New MultipartMemoryStreamProvider()
        Await Request.Content.ReadAsMultipartAsync(provider)

        Dim part = provider.Contents.FirstOrDefault(
            Function(c) c.Headers.ContentDisposition IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(c.Headers.ContentDisposition.FileName))

        If part Is Nothing Then
            Return BadRequest("a 'file' field is required")
        End If

        Dim originalName = part.Headers.ContentDisposition.FileName.Trim(""""c)
        Dim ext = Path.GetExtension(originalName).ToLowerInvariant()

        If Not AllowedExtensions.Contains(ext) Then
            Return Content(HttpStatusCode.UnsupportedMediaType, New JObject From {
                {"error", $"File type '{ext}' is not allowed."}
            })
        End If

        Dim bytes = Await part.ReadAsByteArrayAsync()
        Dim newName = Guid.NewGuid().ToString("N") & ext

        Dim uploadsDir = HostingEnvironment.MapPath("~/uploads")
        If Not Directory.Exists(uploadsDir) Then
            Directory.CreateDirectory(uploadsDir)
        End If

        Dim filePath = Path.Combine(uploadsDir, newName)
        File.WriteAllBytes(filePath, bytes)

        Return Ok(New JObject From {
            {"file_url", "/api/uploads/" & newName}
        })
    End Function

End Class
