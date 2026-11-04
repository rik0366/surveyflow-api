Imports System.Configuration
Imports System.Data.SqlClient

' Thin ADO.NET wrapper. Caller is responsible for disposing the returned
' SqlConnection — wrap the call in `Using conn = Db.Open()`.
Public Module Db

    Private Const ConnectionStringName As String = "SurveyFlow"

    ' Opens a new SqlConnection using the "SurveyFlow" connection string.
    Public Function Open() As SqlConnection
        Dim entry = ConfigurationManager.ConnectionStrings(ConnectionStringName)
        If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.ConnectionString) Then
            Throw New InvalidOperationException(
                "Connection string '" & ConnectionStringName & "' is missing or empty in Web.config.")
        End If

        Dim conn As New SqlConnection(entry.ConnectionString)
        conn.Open()
        Return conn
    End Function

    ' Legacy PIQ (PeriscopeIQ) database, read-only — used only by the
    ' "Import from PIQ" survey importer. Optional: returns Nothing when the
    ' "PIQ" connection string isn't configured, so nothing else in the API
    ' depends on it.
    Public Function OpenPiq() As SqlConnection
        Dim entry = ConfigurationManager.ConnectionStrings("PIQ")
        If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.ConnectionString) Then Return Nothing
        Dim conn As New SqlConnection(entry.ConnectionString)
        conn.Open()
        Return conn
    End Function

End Module
