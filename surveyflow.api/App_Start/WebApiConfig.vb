Imports System.Configuration
Imports System.Web.Http
Imports System.Web.Http.Cors
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Serialization

Public Module WebApiConfig
    Public Sub Register(ByVal config As HttpConfiguration)

        ' --- CORS for the React dev server ---------------------------------
        ' Origin list comes from Web.config so dev/prod can differ without code
        ' change. In production we host the React build same-origin with the
        ' API, so CORS isn't needed and the setting is left blank — skip
        ' enabling CORS in that case (EnableCorsAttribute rejects empty origins).
        ' SupportsCredentials=True is required for cookie-based auth, which
        ' also forbids "*" — origins must be explicit.
        Dim origins = ConfigurationManager.AppSettings("Cors:AllowedOrigins")
        If Not String.IsNullOrWhiteSpace(origins) Then
            Dim cors As New EnableCorsAttribute(origins, "*", "*")
            cors.SupportsCredentials = True
            config.EnableCors(cors)
        End If

        ' --- JSON: snake_case to match the React code's field expectations -----
        Dim json = config.Formatters.JsonFormatter
        json.SerializerSettings.ContractResolver = New DefaultContractResolver With {
            .NamingStrategy = New SnakeCaseNamingStrategy()
        }
        json.SerializerSettings.NullValueHandling = NullValueHandling.Include
        json.SerializerSettings.DateFormatHandling = DateFormatHandling.IsoDateFormat
        json.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc

        ' Drop the XML formatter — we only speak JSON.
        config.Formatters.Remove(config.Formatters.XmlFormatter)

        ' --- Authorization --------------------------------------------------
        ' Default-deny: every action requires an authenticated user. Specific
        ' actions can opt out with [AllowAnonymous] (e.g. login, set-password,
        ' take-survey paths, health check). This is safer than "default allow,
        ' opt in to auth" because forgetting an [Authorize] would otherwise
        ' silently expose a new endpoint.
        config.Filters.Add(New AuthorizeAttribute())

        ' --- Routing --------------------------------------------------------
        config.MapHttpAttributeRoutes()

        ' No "api/" prefix here. The "/api" path segment in URLs comes from
        ' how the API is hosted (sub-application alias in production, Vite
        ' proxy in development). Both strip the "/api" before the request
        ' reaches the routing layer, so route templates and Route attributes
        ' should not include it.
        config.Routes.MapHttpRoute(
            name:="DefaultApi",
            routeTemplate:="{controller}/{id}",
            defaults:=New With {.id = RouteParameter.Optional}
        )
    End Sub
End Module
