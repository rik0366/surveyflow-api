' Reads the authenticated user's email from the current request.
'
' Forms auth stores the username we passed to FormsAuthentication.SetAuthCookie
' (the user's email) in HttpContext.Current.User.Identity.Name. Returns
' "anonymous" for unauthenticated requests — used so anonymous survey
' respondents can still POST a SurveyResponse without erroring.
Public Module CurrentUser

    Public Function Email() As String
        Dim ctx = System.Web.HttpContext.Current
        If ctx IsNot Nothing AndAlso
           ctx.User IsNot Nothing AndAlso
           ctx.User.Identity IsNot Nothing AndAlso
           ctx.User.Identity.IsAuthenticated Then
            Return ctx.User.Identity.Name
        End If
        Return "anonymous"
    End Function

End Module
