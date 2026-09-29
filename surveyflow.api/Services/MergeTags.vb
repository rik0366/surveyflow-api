Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' Email merge tags for server-side sends (scheduled reminders). Mirrors
' src/lib/mergeTags.js in the React app — keep the two in sync:
'
'   {{first_name}}            value from the invite
'   {{first_name|there}}      …or "there" when blank
'   {{custom.region}}         custom field snapshot on the invite
'   {{question}}              left alone
'
' Tag names are case-insensitive; unknown tags are left in place; legacy
' PIQ tags ({{FN}}, {{LN}}, {{auth_password}}, {{add survey link}}, …) are
' accepted. Output is plain text — callers HTML-encode the whole body.
Public Module MergeTags

    Public Class Recipient
        Public Property Email As String
        Public Property FirstName As String
        Public Property LastName As String
        Public Property Company As String
        Public Property IdNumber As String
        Public Property Password As String
        Public Property RespondentId As Long?
        Public Property Custom As JObject
    End Class

    Private ReadOnly TagPattern As New Regex("\{\{\s*([\w. ]+?)\s*(?:\|([^}]*))?\}\}", RegexOptions.Compiled)

    Private ReadOnly Aliases As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
        {"fn", "first_name"}, {"firstname", "first_name"},
        {"ln", "last_name"}, {"lastname", "last_name"},
        {"full_name", "recipient_name"}, {"name", "recipient_name"},
        {"email", "recipient_email"},
        {"custom_id", "id_number"}, {"idnumber", "id_number"}, {"add idnumber", "id_number"}, {"private_password", "id_number"},
        {"auth_password", "password"},
        {"add survey link", "survey_link"}, {"link", "survey_link"}
    }

    Public Function Resolve(template As String, surveyTitle As String, surveyLink As String, r As Recipient) As String
        If String.IsNullOrEmpty(template) Then Return ""
        Return TagPattern.Replace(template,
            Function(m As Match) As String
                Dim name = m.Groups(1).Value.Trim().ToLowerInvariant()
                If name = "question" Then Return m.Value
                Dim v = Lookup(name, surveyTitle, surveyLink, r)
                If v Is Nothing Then Return m.Value
                If v.Length = 0 AndAlso m.Groups(2).Success Then Return m.Groups(2).Value.Trim()
                Return v
            End Function)
    End Function

    ' Value for a tag, or Nothing when the tag isn't recognised.
    Private Function Lookup(name As String, title As String, link As String, r As Recipient) As String
        Dim key As String = Nothing
        If Not Aliases.TryGetValue(name, key) Then key = name
        If key.StartsWith("custom.") Then
            Dim v = r?.Custom?(key.Substring(7))
            Return If(v Is Nothing OrElse v.Type = JTokenType.Null, "", v.ToString())
        End If
        Select Case key
            Case "survey_title" : Return If(String.IsNullOrEmpty(title), "Survey", title)
            Case "survey_link" : Return If(link, "")
            Case "first_name" : Return If(r?.FirstName, "")
            Case "last_name" : Return If(r?.LastName, "")
            Case "recipient_name" : Return String.Join(" ", {r?.FirstName, r?.LastName}.Where(Function(s) Not String.IsNullOrEmpty(s)))
            Case "recipient_email" : Return If(r?.Email, "")
            Case "company" : Return If(r?.Company, "")
            Case "id_number" : Return If(r?.IdNumber, "")
            Case "password" : Return If(r?.Password, "")
            Case "respondent_id" : Return If(r IsNot Nothing AndAlso r.RespondentId.HasValue, r.RespondentId.Value.ToString(), "")
            Case Else : Return Nothing
        End Select
    End Function

End Module
