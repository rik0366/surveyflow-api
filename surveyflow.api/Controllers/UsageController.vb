Imports System.Configuration
Imports System.Data
Imports System.Data.SqlClient
Imports System.Globalization
Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Admin-only billing/usage report.
'
' Routes:
'   GET /api/admin/usage-report?from=YYYY-MM-DD&to=YYYY-MM-DD
'
' Returns:
'   {
'     "from": "...", "to": "...",
'     "rates": { "prompt_per_1m": 0.15, "completion_per_1m": 0.60 },
'     "users": [
'       { "email", "full_name", "role",
'         "survey_count", "response_count",
'         "ai_call_count", "prompt_tokens", "completion_tokens", "total_tokens",
'         "estimated_cost",
'         "surveys": [ { "id", "title", "created_date",
'                        "response_count", "ai_call_count", "total_tokens", "estimated_cost" }, ... ]
'       }, ...
'     ],
'     "totals": { same shape as a user row, summed }
'   }
Public Class UsageController
    Inherits ApiController

    ' Published gpt-4o-mini pricing (per 1M tokens) as of 2025. Tweak in
    ' AppSettings via "Ai:PromptCostPer1M" / "Ai:CompletionCostPer1M" to track
    ' future price changes without redeploying.
    Private Shared ReadOnly DefaultPromptCostPer1M As Double = 0.15
    Private Shared ReadOnly DefaultCompletionCostPer1M As Double = 0.6


    <HttpGet, Route("admin/usage-report")>
    Public Function UsageReport(Optional [from] As String = Nothing, Optional [to] As String = Nothing) As IHttpActionResult
        ' Admin-only
        Dim callerEmail = If(System.Web.HttpContext.Current?.User?.Identity?.Name, "")
        Dim role = LookupRole(callerEmail)
        If Not String.Equals(role, "admin", StringComparison.OrdinalIgnoreCase) Then
            Return StatusCode(HttpStatusCode.Forbidden)
        End If

        ' Date range. Default: previous 30 days.
        Dim toDate As DateTime
        Dim fromDate As DateTime
        If Not TryParseDate([to], DateTime.UtcNow, toDate) Then Return BadRequest("Invalid 'to' date.")
        If Not TryParseDate([from], toDate.AddDays(-30), fromDate) Then Return BadRequest("Invalid 'from' date.")
        ' Use [from, to+1day) to make "to" inclusive of its full calendar day.
        Dim toExclusive = toDate.Date.AddDays(1)
        Dim fromInclusive = fromDate.Date

        Dim promptRate = GetRate("Ai:PromptCostPer1M", DefaultPromptCostPer1M)
        Dim completionRate = GetRate("Ai:CompletionCostPer1M", DefaultCompletionCostPer1M)

        ' Per-user roll-up
        Dim userRows = LoadUserAggregates(fromInclusive, toExclusive)
        ' Per-survey roll-up (used to nest under each user)
        Dim surveyRows = LoadSurveyAggregates(fromInclusive, toExclusive)

        Dim surveysByOwner As New Dictionary(Of String, JArray)(StringComparer.OrdinalIgnoreCase)
        For Each sr In surveyRows
            Dim owner = sr.Value(Of String)("created_by")
            If String.IsNullOrEmpty(owner) Then Continue For
            Dim list As JArray = Nothing
            If Not surveysByOwner.TryGetValue(owner, list) Then
                list = New JArray
                surveysByOwner(owner) = list
            End If
            sr("estimated_cost") = EstimateCost(sr, promptRate, completionRate)
            sr.Remove("created_by")
            list.Add(sr)
        Next

        Dim usersOut As New JArray
        Dim tSurvey = 0, tResp = 0, tCalls = 0, tPrompt As Long = 0, tComp As Long = 0, tTotal As Long = 0
        Dim tCost As Double = 0

        For Each ur In userRows
            ur("estimated_cost") = EstimateCost(ur, promptRate, completionRate)
            Dim email = ur.Value(Of String)("email")
            Dim ss As JArray = Nothing
            If surveysByOwner.TryGetValue(email, ss) Then ur("surveys") = ss Else ur("surveys") = New JArray

            tSurvey += ur.Value(Of Integer)("survey_count")
            tResp += ur.Value(Of Integer)("response_count")
            tCalls += ur.Value(Of Integer)("ai_call_count")
            tPrompt += ur.Value(Of Long)("prompt_tokens")
            tComp += ur.Value(Of Long)("completion_tokens")
            tTotal += ur.Value(Of Long)("total_tokens")
            tCost += ur.Value(Of Double)("estimated_cost")

            usersOut.Add(ur)
        Next

        Return Ok(New JObject From {
            {"from", fromInclusive.ToString("yyyy-MM-dd")},
            {"to",   toDate.Date.ToString("yyyy-MM-dd")},
            {"rates", New JObject From {
                {"model", If(ConfigurationManager.AppSettings("Ai:Model"), "gpt-4o-mini")},
                {"prompt_per_1m", promptRate},
                {"completion_per_1m", completionRate}
            }},
            {"users", usersOut},
            {"totals", New JObject From {
                {"survey_count", tSurvey},
                {"response_count", tResp},
                {"ai_call_count", tCalls},
                {"prompt_tokens", tPrompt},
                {"completion_tokens", tComp},
                {"total_tokens", tTotal},
                {"estimated_cost", Math.Round(tCost, 4)}
            }}
        })
    End Function


    ' --- helpers ----------------------------------------------------------

    Private Shared Function TryParseDate(s As String, fallback As DateTime, ByRef result As DateTime) As Boolean
        If String.IsNullOrWhiteSpace(s) Then
            result = fallback
            Return True
        End If
        Dim parsed As DateTime
        If DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, parsed) Then
            result = parsed
            Return True
        End If
        Return False
    End Function

    Private Shared Function GetRate(key As String, fallback As Double) As Double
        Dim raw = ConfigurationManager.AppSettings(key)
        Dim v As Double
        If Not String.IsNullOrWhiteSpace(raw) AndAlso Double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, v) Then Return v
        Return fallback
    End Function

    Private Shared Function EstimateCost(row As JObject, promptRate As Double, completionRate As Double) As Double
        Dim pt = row.Value(Of Long?)("prompt_tokens").GetValueOrDefault()
        Dim ct = row.Value(Of Long?)("completion_tokens").GetValueOrDefault()
        Return Math.Round(pt * promptRate / 1000000.0 + ct * completionRate / 1000000.0, 4)
    End Function

    Private Shared Function LookupRole(email As String) As String
        If String.IsNullOrEmpty(email) Then Return ""
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT role FROM dbo.Users WHERE email = @e"
                cmd.Parameters.Add(New SqlParameter("@e", SqlDbType.NVarChar, 320) With {.Value = email})
                Return TryCast(cmd.ExecuteScalar(), String)
            End Using
        End Using
    End Function

    Private Shared Function LoadUserAggregates(fromInclusive As DateTime, toExclusive As DateTime) As List(Of JObject)
        Dim out As New List(Of JObject)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT
                        u.email,
                        u.full_name,
                        u.role,
                        COALESCE(s.survey_count, 0)       AS survey_count,
                        COALESCE(r.response_count, 0)    AS response_count,
                        COALESCE(a.ai_call_count, 0)     AS ai_call_count,
                        COALESCE(a.prompt_tokens, 0)     AS prompt_tokens,
                        COALESCE(a.completion_tokens, 0) AS completion_tokens,
                        COALESCE(a.total_tokens, 0)      AS total_tokens
                    FROM dbo.Users u
                    LEFT JOIN (
                        SELECT created_by, COUNT(*) AS survey_count
                        FROM dbo.Surveys
                        WHERE created_date >= @f AND created_date < @t
                        GROUP BY created_by
                    ) s ON s.created_by = u.email
                    LEFT JOIN (
                        SELECT s.created_by, COUNT(*) AS response_count
                        FROM dbo.SurveyResponses sr
                        INNER JOIN dbo.Surveys s ON sr.survey_id = s.id
                        WHERE sr.created_date >= @f AND sr.created_date < @t
                        GROUP BY s.created_by
                    ) r ON r.created_by = u.email
                    LEFT JOIN (
                        SELECT user_email,
                               COUNT(*) AS ai_call_count,
                               SUM(CAST(COALESCE(prompt_tokens, 0)     AS BIGINT)) AS prompt_tokens,
                               SUM(CAST(COALESCE(completion_tokens, 0) AS BIGINT)) AS completion_tokens,
                               SUM(CAST(COALESCE(total_tokens, 0)      AS BIGINT)) AS total_tokens
                        FROM dbo.AiUsage
                        WHERE called_at >= @f AND called_at < @t AND success = 1
                        GROUP BY user_email
                    ) a ON a.user_email = u.email
                    WHERE u.is_active = 1
                    ORDER BY u.email"
                cmd.Parameters.Add(New SqlParameter("@f", SqlDbType.DateTime2) With {.Value = fromInclusive})
                cmd.Parameters.Add(New SqlParameter("@t", SqlDbType.DateTime2) With {.Value = toExclusive})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(New JObject From {
                            {"email",             rdr.GetString(0)},
                            {"full_name",         If(rdr.IsDBNull(1), Nothing, rdr.GetString(1))},
                            {"role",              If(rdr.IsDBNull(2), "user", rdr.GetString(2))},
                            {"survey_count",      rdr.GetInt32(3)},
                            {"response_count",    rdr.GetInt32(4)},
                            {"ai_call_count",     rdr.GetInt32(5)},
                            {"prompt_tokens",     rdr.GetInt64(6)},
                            {"completion_tokens", rdr.GetInt64(7)},
                            {"total_tokens",      rdr.GetInt64(8)}
                        })
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    Private Shared Function LoadSurveyAggregates(fromInclusive As DateTime, toExclusive As DateTime) As List(Of JObject)
        Dim out As New List(Of JObject)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT
                        s.id,
                        s.title,
                        s.created_by,
                        s.created_date,
                        COALESCE(r.response_count, 0)    AS response_count,
                        COALESCE(a.ai_call_count, 0)     AS ai_call_count,
                        COALESCE(a.prompt_tokens, 0)     AS prompt_tokens,
                        COALESCE(a.completion_tokens, 0) AS completion_tokens,
                        COALESCE(a.total_tokens, 0)      AS total_tokens
                    FROM dbo.Surveys s
                    LEFT JOIN (
                        SELECT survey_id, COUNT(*) AS response_count
                        FROM dbo.SurveyResponses
                        WHERE created_date >= @f AND created_date < @t
                        GROUP BY survey_id
                    ) r ON r.survey_id = s.id
                    LEFT JOIN (
                        SELECT survey_id,
                               COUNT(*) AS ai_call_count,
                               SUM(CAST(COALESCE(prompt_tokens, 0)     AS BIGINT)) AS prompt_tokens,
                               SUM(CAST(COALESCE(completion_tokens, 0) AS BIGINT)) AS completion_tokens,
                               SUM(CAST(COALESCE(total_tokens, 0)      AS BIGINT)) AS total_tokens
                        FROM dbo.AiUsage
                        WHERE called_at >= @f AND called_at < @t AND success = 1 AND survey_id IS NOT NULL
                        GROUP BY survey_id
                    ) a ON a.survey_id = s.id
                    WHERE s.created_date >= @f AND s.created_date < @t
                    ORDER BY s.created_by, s.created_date DESC"
                cmd.Parameters.Add(New SqlParameter("@f", SqlDbType.DateTime2) With {.Value = fromInclusive})
                cmd.Parameters.Add(New SqlParameter("@t", SqlDbType.DateTime2) With {.Value = toExclusive})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(New JObject From {
                            {"id",                rdr.GetString(0)},
                            {"title",             If(rdr.IsDBNull(1), Nothing, rdr.GetString(1))},
                            {"created_by",        If(rdr.IsDBNull(2), Nothing, rdr.GetString(2))},
                            {"created_date",      rdr.GetDateTime(3)},
                            {"response_count",    rdr.GetInt32(4)},
                            {"ai_call_count",     rdr.GetInt32(5)},
                            {"prompt_tokens",     rdr.GetInt64(6)},
                            {"completion_tokens", rdr.GetInt64(7)},
                            {"total_tokens",      rdr.GetInt64(8)}
                        })
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

End Class
