Imports System.Data

' Per-table configuration consumed by JsonRepository.
'
' ScalarColumns map directly to dedicated DB columns (typed).
' JsonColumns hold nested structures stored as NVARCHAR(MAX) JSON.
' FilterableColumns / SortableColumns whitelist what the API accepts in
' query strings — guards against SQL identifier injection via filter keys.
Public Class EntityDescriptor
    Public Property TableName As String
    Public Property ScalarColumns As New List(Of ScalarColumn)
    Public Property JsonColumns As New List(Of JsonColumn)
    Public Property FilterableColumns As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Public Property SortableColumns As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Public Property RequireCreatedBy As Boolean = True

    ' Optional catch-all JSON column. When set, JsonRepository writes the
    ' entire incoming `data` object into this column so any field that
    ' isn't mapped to a dedicated scalar/JSON column still round-trips.
    ' On read, the parsed contents are merged into the output (mapped
    ' columns take precedence over the catch-all).
    Public Property CatchAllJsonColumn As String = Nothing
End Class

Public Class ScalarColumn
    Public Property ColumnName As String         ' DB column, e.g. "title"
    Public Property JsonName As String           ' wire field name, usually same
    Public Property DbType As SqlDbType
    Public Property MaxLength As Integer = -1    ' -1 = use driver default
End Class

Public Class JsonColumn
    Public Property ColumnName As String         ' DB column, e.g. "questions_json"
    Public Property JsonName As String           ' wire field name, e.g. "questions"
End Class
