Imports System.Data
Imports System.Data.SqlClient
Imports System.Linq
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

' Converts one legacy PIQ survey (PeriscopeIQ DB, keyed by Class_ID) into a
' SurveyFlow survey definition: questions, choices, grids, forms,
' spreadsheets, page breaks, required flags, piping and skip logic.
' Responses are NOT imported.
'
' Decoded from PIQ's renderer (generatesurvey.asp / Library_Questions.asp):
'   * What a question renders as is decided by Question.sel, with
'     Question_Type only a coarse group (3 = text-only heading).
'   * Answer choices are Response rows; Controls: 2 radio, 12 radio+other,
'     3 checkbox, 13 checkbox+other, 4 dropdown, 1/11 text box, 5/15
'     comment box, 7 date, 25 matrix-row "other" placeholder.
'   * Grid rows / form fields / spreadsheet cells are child Question rows
'     (Parent_ID); grid columns are the FIRST row's Response rows.
'   * Page_Break = 1 means "break after this question".
'   * Skips: Response.Next_Question (only on a page's last question, i.e.
'     Page_Break=1), then Question.Next_Question, then AdvancedSkip rules
'     "QID,RID;!QID,RID;" (ANDed; ! = not selected). -1 = end of survey.
'   * Exclusive checkbox choices (Input_Validation = 2): positions listed
'     in Validation_Pattern, else the last choice.
'   * Piping: piping_replace_phrase is replaced with the answer to
'     piping_replacement_question_id.
' Anything that can't be carried over exactly is reported in Warnings so
' the admin can fix it in the builder.
Public Class PiqImportResult
    Public Property ClassId As Integer
    Public Property Title As String
    Public Property Survey As JObject           ' title / description / questions / settings
    Public Property Warnings As New List(Of String)
    Public Property QuestionCount As Integer
    Public Property PageCount As Integer
    Public Property TypeCounts As New SortedDictionary(Of String, Integer)
End Class

Public Class PiqImportService

    Private Shared ReadOnly Rng As New Random()
    Private Shared ReadOnly IdChars As String = "abcdefghijklmnopqrstuvwxyz0123456789"

    Private Shared Function NewId() As String
        SyncLock Rng
            Return New String(Enumerable.Range(0, 8).Select(Function(i) IdChars(Rng.Next(IdChars.Length))).ToArray())
        End SyncLock
    End Function

    ' ---- loaded PIQ data ------------------------------------------------
    Private _cls As DataRow
    Private _questions As DataTable
    Private _responses As DataTable
    Private _skips As DataTable
    Private ReadOnly _children As New Dictionary(Of Integer, List(Of DataRow))
    Private ReadOnly _respByQ As New Dictionary(Of Integer, List(Of DataRow))
    Private ReadOnly _respById As New Dictionary(Of Integer, DataRow)
    Private ReadOnly _qById As New Dictionary(Of Integer, DataRow)
    Private _top As List(Of DataRow)

    ' ---- conversion state -----------------------------------------------
    Private ReadOnly _sfId As New Dictionary(Of Integer, String)      ' PIQ top-level QID -> SurveyFlow id
    Private ReadOnly _sfQ As New Dictionary(Of Integer, JObject)      ' PIQ top-level QID -> converted question
    Private ReadOnly _num As New Dictionary(Of Integer, Integer)       ' PIQ QID -> respondent-visible number
    Private ReadOnly _result As New PiqImportResult()

    Public Shared Function Convert(classId As Integer) As PiqImportResult
        Dim svc As New PiqImportService()
        Return svc.Run(classId)
    End Function

    Private Function Run(classId As Integer) As PiqImportResult
        _result.ClassId = classId
        Load(classId)
        If _cls Is Nothing Then Return Nothing

        ' Respondent-visible numbering (headings and hidden fields aren't numbered).
        Dim n = 0
        For Each q In _top
            If Not IsHeading(q) AndAlso Sel(q) <> 21 Then n += 1 : _num(QidOf(q)) = n
        Next

        For Each q In _top
            _sfId(QidOf(q)) = NewId()
        Next

        Dim questions As New JArray
        For Each q In _top
            Dim converted = ConvertQuestion(q)
            If converted Is Nothing Then Continue For
            _sfQ(QidOf(q)) = converted
            questions.Add(converted)
        Next

        ApplyPiping()
        ApplySkips()

        ' A trailing page break is meaningless; PIQ often sets one.
        If questions.Count > 0 Then CType(questions.Last, JObject)("page_break_after") = False

        Dim instructions = CleanText(Str(_cls, "Instructions"))
        Dim settings As New JObject From {
            {"show_progress_bar", Not Bool(_cls, "No_Progress_Bar")},
            {"imported_from_piq", New JObject From {{"class_id", classId}, {"imported_at", DateTime.UtcNow.ToString("o")}}}
        }
        If instructions <> "" Then settings("welcome_message") = instructions
        Dim thanks = CleanText(Str(_cls, "ThankYou_Message"))
        If Bool(_cls, "ThankYou_Custom") AndAlso thanks <> "" Then settings("completion_message") = thanks

        _result.Title = CleanText(Str(_cls, "Title"))
        If _result.Title = "" Then _result.Title = "PIQ survey " & classId
        _result.Survey = New JObject From {
            {"title", _result.Title},
            {"description", CleanText(Str(_cls, "Sub_Title"))},
            {"questions", questions},
            {"settings", settings}
        }
        _result.Warnings = _result.Warnings.Distinct().ToList()
        _result.QuestionCount = questions.Count
        _result.PageCount = 1 + questions.Cast(Of JObject)().Count(Function(x) x.Value(Of Boolean?)("page_break_after").GetValueOrDefault())
        For Each x As JObject In questions
            Dim t = x.Value(Of String)("type")
            _result.TypeCounts(t) = If(_result.TypeCounts.ContainsKey(t), _result.TypeCounts(t), 0) + 1
        Next
        Return _result
    End Function

    ' ===================================================================
    ' Loading
    ' ===================================================================
    Private Sub Load(classId As Integer)
        Using conn = Db.OpenPiq()
            If conn Is Nothing Then Throw New InvalidOperationException("The PIQ database isn't configured on this server (connection string 'PIQ').")
            Dim clsT = Query(conn, "SELECT * FROM Class WHERE Class_ID = @c", classId)
            If clsT.Rows.Count = 0 Then Return
            _cls = clsT.Rows(0)
            _questions = Query(conn, "SELECT Question_ID, Parent_ID, Position, Question_Type, sel, Format, Layout, ColumnHeaders, Required, Page_Break, IsHidden, Next_Question, piping_replace_phrase, piping_replacement_question_id, CAST(Question AS nvarchar(max)) AS QText, CAST(Instructions AS nvarchar(max)) AS QInstr, Input_Validation, Validation_Pattern, constant_sum, MinValue, MaxValue, RANDOMIZE_RESPONSES, Alphabetize_Responses FROM Question WHERE Class_ID = @c ORDER BY Position, Question_ID", classId)
            _responses = Query(conn, "SELECT r.Response_ID, r.Question_ID, r.Controls, r.Response_Text, r.Position, r.Next_Question, r.HideResponse FROM Response r JOIN Question q ON q.Question_ID = r.Question_ID WHERE q.Class_ID = @c ORDER BY r.Question_ID, r.Position, r.Response_ID", classId)
            _skips = Query(conn, "SELECT s.Skip_ID, s.QFrom, s.QTo, s.[Rule], s.ExtData, s.SkipOrder FROM AdvancedSkip s WHERE s.Class_ID = @c", classId)
        End Using

        For Each q As DataRow In _questions.Rows
            _qById(QidOf(q)) = q
            If Not IsDBNull(q("Parent_ID")) Then
                Dim p = CInt(q("Parent_ID"))
                If Not _children.ContainsKey(p) Then _children(p) = New List(Of DataRow)
                _children(p).Add(q)
            End If
        Next
        For Each r As DataRow In _responses.Rows
            Dim qid = CInt(r("Question_ID"))
            If Not _respByQ.ContainsKey(qid) Then _respByQ(qid) = New List(Of DataRow)
            _respByQ(qid).Add(r)
            _respById(CInt(r("Response_ID"))) = r
        Next

        Dim all = _questions.Rows.Cast(Of DataRow)().Where(Function(q) IsDBNull(q("Parent_ID"))).ToList()
        Dim hidden = all.Where(Function(q) Bool(q, "IsHidden")).ToList()
        For Each h In hidden
            Warn(Nothing, $"A hidden question (PIQ {QidOf(h)}, reachable only by a skip) was not imported: ""{Snippet(Str(h, "QText"))}"".")
        Next
        _top = all.Except(hidden).ToList()
    End Sub

    Private Shared Function Query(conn As SqlConnection, sql As String, classId As Integer) As DataTable
        Using cmd = conn.CreateCommand()
            cmd.CommandText = sql
            cmd.CommandTimeout = 120
            cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Int) With {.Value = classId})
            Dim t As New DataTable()
            Using rdr = cmd.ExecuteReader()
                t.Load(rdr)
            End Using
            Return t
        End Using
    End Function

    ' ===================================================================
    ' Questions
    ' ===================================================================
    Private Function ConvertQuestion(q As DataRow) As JObject
        Dim qid = QidOf(q)
        Dim s = Sel(q)
        Dim kids = ChildRows(qid)
        Dim o As New JObject From {
            {"id", _sfId(qid)},
            {"text", CleanText(Str(q, "QText"))},
            {"description", CleanText(Str(q, "QInstr"))},
            {"required", Int(q, "Required") = 1},
            {"page_break_after", Bool(q, "Page_Break")},
            {"branching_rules", New JArray()}
        }

        If IsHeading(q) Then
            o("type") = "section_text" : o("required") = False
            Return o
        End If

        Select Case s
            Case 0
                o("type") = "short_text"
                AppendTextBoxLabel(o, qid)
            Case 7
                o("type") = "long_text"
                AppendTextBoxLabel(o, qid)
            Case 20
                o("type") = "date"
            Case 1, 4, 5, 10
                ConvertChoice(q, o, s)
            Case 6, 14, 26
                ConvertGrid(q, o, s, kids)
            Case 15
                ConvertCheckboxGrid(q, o, kids)
            Case 9, 11, 18
                ConvertForm(q, o, s, kids)
            Case 12
                o("type") = "rank_order"
                o("options") = New JArray(kids.Select(Function(k) CleanText(Str(k, "QText"))).ToArray())
            Case 13
                o("type") = "percent_allocation"
                o("options") = New JArray(kids.Select(Function(k) CleanText(Str(k, "QText"))).ToArray())
                Dim total = Int(q, "constant_sum")
                o("allocation_must_total_100") = (total = 100)
                If total <> 100 Then Warn(q, $"constant sum target was {If(total = 0, "a min/max range", total.ToString())}; SurveyFlow allocations total up to 100. Check the wording.")
            Case 19
                ConvertSpreadsheet(q, o, kids)
            Case 25
                o("type") = "short_text"
                Warn(q, "was a URL-prefilled text box; imported as an ordinary text box.")
            Case 21
                Warn(q, "was a hidden data field (value from the survey link); not imported.")
                Return Nothing
            Case Else
                o("type") = "section_text" : o("required") = False
                Warn(q, $"uses a PIQ question style SurveyFlow doesn't have (sel {s}); imported as text only — rebuild it in the builder.")
        End Select
        Return o
    End Function

    ' Single choice / dropdown / checkboxes, including "Other, please specify",
    ' comment boxes, exclusive choices and checkbox-count limits.
    Private Sub ConvertChoice(q As DataRow, o As JObject, s As Integer)
        Dim qid = QidOf(q)
        Dim choiceControls As Integer() = If(s = 4, {4}, If(s = 5, {3, 13}, {2, 12}))
        o("type") = If(s = 4, "dropdown", If(s = 5, "checkbox", "multiple_choice"))

        Dim options As New JArray, specify As New JArray, headers = 0
        Dim byPos As New Dictionary(Of Integer, String)
        For Each r In Resps(qid)
            Dim c = Int(r, "Controls")
            If Not choiceControls.Contains(c) Then Continue For
            If Bool(r, "HideResponse") Then Continue For
            Dim label = CleanText(Str(r, "Response_Text"))
            If label.StartsWith("&") Then headers += 1 : Continue For
            If label = "" Then Continue For
            If options.Any(Function(x) x.ToString() = label) Then
                Warn(q, $"has the choice ""{Snippet(label)}"" twice; kept once.") : Continue For
            End If
            options.Add(label)
            byPos(Int(r, "Position")) = label
            If c = 12 OrElse c = 13 Then specify.Add(label)
        Next
        If headers > 0 Then Warn(q, $"had {headers} header line(s) inside its choice list; SurveyFlow has no choice headers, so they were left out.")

        ' Choice order: Format part 2 (1 as entered, 2 alphabetical, 3/4 random/rotate).
        Dim order = FormatPart(q, 1)
        If order = "2" OrElse (order = "" AndAlso Int(q, "RANDOMIZE_RESPONSES") = 2) Then
            options = New JArray(options.Select(Function(x) x.ToString()).OrderBy(Function(x) Regex.Replace(x, "<[^>]+>", ""), StringComparer.OrdinalIgnoreCase).ToArray())
        ElseIf order = "3" OrElse order = "4" OrElse Int(q, "RANDOMIZE_RESPONSES") < 0 OrElse Int(q, "RANDOMIZE_RESPONSES") > 2 Then
            Warn(q, "showed its choices in random/rotated order in PIQ; imported in the entered order.")
        End If

        o("options") = options
        If specify.Count > 0 Then o("options_specify") = specify

        ' Comment box: an extra Controls 5/15 response on a choice question.
        Dim comment = Resps(qid).FirstOrDefault(Function(r) Int(r, "Controls") = 5 OrElse Int(r, "Controls") = 15)
        If comment IsNot Nothing Then
            Dim label = CleanText(Str(comment, "Response_Text"))
            o("comment_box") = New JObject From {{"enabled", True}, {"label", If(label = "", "Comments", label)}}
        End If

        If s = 5 Then
            Dim iv = Int(q, "Input_Validation")
            Dim vp = Str(q, "Validation_Pattern").Trim()
            If iv = 2 Then
                ' Exclusive choices: positions in Validation_Pattern, else the last choice.
                Dim positions = Regex.Split(vp, "[,|]").Select(Function(p) p.Trim()).Where(Function(p) p <> "").Select(Function(p) CInt(Val(p))).ToList()
                If positions.Count = 0 AndAlso byPos.Count > 0 Then positions.Add(byPos.Keys.Max())
                Dim excl = positions.Where(Function(p) byPos.ContainsKey(p)).Select(Function(p) byPos(p)).Distinct().ToArray()
                If excl.Length > 0 Then o("options_exclusive") = New JArray(excl)
            ElseIf (iv = 1 OrElse iv = 3) AndAlso vp.Contains("|") Then
                ' Checkbox count rule "op|n[|m]": 1 exactly, 2 at least, 3 at most, 4 between.
                Dim parts = vp.Split("|"c)
                Dim op = CInt(Val(parts(0))), n1 = CInt(Val(parts(1)))
                Dim n2 = If(parts.Length > 2, CInt(Val(parts(2))), 0)
                If op = 3 AndAlso n1 > 0 Then
                    o("max_selections") = n1
                ElseIf op = 4 AndAlso n2 > 0 Then
                    o("max_selections") = n2
                    If n1 > 0 Then Warn(q, $"required at least {n1} selection(s); SurveyFlow only limits the maximum.")
                ElseIf op = 1 AndAlso n1 > 0 Then
                    o("max_selections") = n1
                    Warn(q, $"required exactly {n1} selection(s); imported as ""at most {n1}"".")
                ElseIf op = 2 Then
                    Warn(q, $"required at least {n1} selection(s); SurveyFlow can't enforce a minimum.")
                End If
            End If
        End If
        If options.Count = 0 Then Warn(q, "has no answer choices.")
    End Sub

    ' Radio grid. Rows = child questions, columns = first row's choices.
    Private Sub ConvertGrid(q As DataRow, o As JObject, s As Integer, kids As List(Of DataRow))
        o("type") = "grid"
        o("grid_rows") = New JArray(kids.Select(Function(k) CleanText(Str(k, "QText"))).ToArray())
        o("grid_columns") = New JArray(GridColumns(kids).ToArray())
        If s = 14 OrElse s = 26 Then Warn(q, "was a dual-scale matrix in PIQ; imported as a single grid — check its columns.")
        GridRowOtherWarning(q, kids)
        If kids.Count = 0 Then Warn(q, "is a grid with no rows.")
    End Sub

    ' Checkbox grid → multiple-choice grid (any number of ticks per row).
    Private Sub ConvertCheckboxGrid(q As DataRow, o As JObject, kids As List(Of DataRow))
        o("type") = "grid_multi"
        o("grid_rows") = New JArray(kids.Select(Function(k) CleanText(Str(k, "QText"))).ToArray())
        o("grid_columns") = New JArray(GridColumns(kids).ToArray())
        GridRowOtherWarning(q, kids)
        If kids.Count = 0 Then Warn(q, "is a grid with no rows.")
    End Sub

    Private Function GridColumns(kids As List(Of DataRow)) As List(Of String)
        If kids.Count = 0 Then Return New List(Of String)
        Return Resps(QidOf(kids(0))).
            Where(Function(r) Int(r, "Controls") <> 25 AndAlso Not IsDBNull(r("Response_Text")) AndAlso Not Bool(r, "HideResponse")).
            Select(Function(r) CleanText(Str(r, "Response_Text"))).Where(Function(t) t <> "").Distinct().ToList()
    End Function

    Private Sub GridRowOtherWarning(q As DataRow, kids As List(Of DataRow))
        ' Grid rows overload Instructions as an "other, specify" flag (> 0).
        Dim others = kids.Where(Function(k) Val(Str(k, "QInstr")) > 0).Count()
        If others > 0 Then Warn(q, $"had a write-in box on {others} row(s); add a comments box if you still need it.")
    End Sub

    ' Forms: one field per child row.
    Private Sub ConvertForm(q As DataRow, o As JObject, s As Integer, kids As List(Of DataRow))
        o("type") = "form"
        Dim fields As New JArray
        Dim nonText = 0
        For Each k In kids
            Dim ks = Sel(k)
            If s = 18 AndAlso Not (ks = 0 OrElse ks = 7) Then nonText += 1
            Dim numeric = (s = 11) OrElse Int(k, "Input_Validation") = 1
            fields.Add(New JObject From {
                {"id", NewId()},
                {"label", CleanText(Str(k, "QText"))},
                {"input_type", If(numeric, "number", "text")},
                {"required", Int(k, "Required") = 1 OrElse Int(q, "Required") = 1}
            })
        Next
        o("form_fields") = fields
        o("required") = False   ' per-field required carries it
        If nonText > 0 Then Warn(q, $"was a PIQ form wizard with {nonText} non-text field(s) (choices/dates); they became text fields.")
    End Sub

    ' General spreadsheet: one child per cell, text "Row|Column".
    Private Sub ConvertSpreadsheet(q As DataRow, o As JObject, kids As List(Of DataRow))
        o("type") = "spreadsheet"
        Dim rows As New List(Of String), cols As New List(Of String)
        Dim numericCol As New Dictionary(Of String, Boolean)
        For Each k In kids
            Dim parts = Str(k, "QText").Split({"|"c}, 2)
            Dim r = CleanText(parts(0))
            Dim c = If(parts.Length > 1, CleanText(parts(1)), "")
            If Not rows.Contains(r) Then rows.Add(r)
            If Not cols.Contains(c) Then cols.Add(c)
            If Int(k, "Input_Validation") = 1 Then numericCol(c) = True
        Next
        o("spreadsheet_rows") = New JArray(rows.ToArray())
        o("spreadsheet_columns") = New JArray(cols.Select(Function(c) New JObject From {
            {"id", NewId()}, {"label", c},
            {"cell_type", If(numericCol.ContainsKey(c), "number", "short_text")},
            {"cell_options", New JArray()}}).ToArray())
    End Sub

    ' A PIQ text box can carry a label printed before it (its Response_Text).
    Private Sub AppendTextBoxLabel(o As JObject, qid As Integer)
        Dim label = Resps(qid).Where(Function(r) {1, 5, 11, 15}.Contains(Int(r, "Controls"))).
            Select(Function(r) CleanText(Str(r, "Response_Text"))).FirstOrDefault(Function(t) t <> "")
        If label Is Nothing Then Return
        Dim d = o.Value(Of String)("description")
        o("description") = If(String.IsNullOrEmpty(d), label, d & "<br>" & label)
    End Sub

    ' ===================================================================
    ' Piping: replace PIQ's phrase with a SurveyFlow pipe bound to the
    ' source question's id.
    ' ===================================================================
    Private Sub ApplyPiping()
        For Each q In _top
            Dim phrase = Str(q, "piping_replace_phrase")
            If phrase = "" OrElse Not _sfQ.ContainsKey(QidOf(q)) Then Continue For
            Dim srcQid = Int(q, "piping_replacement_question_id")
            Dim target = _sfQ(QidOf(q))
            If Not _sfId.ContainsKey(srcQid) Then
                Warn(q, $"pipes ""{phrase}"" from a question that wasn't imported; left as plain text.") : Continue For
            End If
            Dim token = "{{q:" & _sfId(srcQid) & "}}"
            target("text") = target.Value(Of String)("text").Replace(phrase, token)
            target("description") = target.Value(Of String)("description").Replace(phrase, token)
        Next
    End Sub

    ' ===================================================================
    ' Skips → SurveyFlow branching rules.
    ' SurveyFlow applies a question's rules when the respondent reaches it,
    ' skipping everything up to the target. PIQ evaluates at the end of a
    ' page, so every rule is attached to the LAST question on its page.
    ' ===================================================================
    Private Sub ApplySkips()
        Dim index As New Dictionary(Of Integer, Integer)
        For i = 0 To _top.Count - 1 : index(QidOf(_top(i))) = i : Next

        ' Last question of the page each top-level question sits on.
        Dim pageLast As New Dictionary(Of Integer, DataRow)
        Dim pageStart = 0
        For i = 0 To _top.Count - 1
            If Bool(_top(i), "Page_Break") OrElse i = _top.Count - 1 Then
                For j = pageStart To i : pageLast(QidOf(_top(j))) = _top(i) : Next
                pageStart = i + 1
            End If
        Next

        For Each q In _top
            If Not Bool(q, "Page_Break") OrElse Not _sfQ.ContainsKey(QidOf(q)) Then Continue For
            Dim holder = _sfQ(QidOf(q))
            ' 1) Choice-level jumps (only on the page's last question, as in PIQ).
            For Each r In Resps(QidOf(q))
                Dim nq = Int(r, "Next_Question")
                If nq = 0 Then Continue For
                Dim label = CleanText(Str(r, "Response_Text"))
                Dim target = ResolveTarget(q, nq, index)
                If target Is Nothing OrElse label = "" Then Continue For
                AddRule(holder, New JArray(New JObject From {{"question_id", holder("id")}, {"answer_value", label}}), target)
            Next
        Next

        ' 2) Advanced skips, in PIQ's order.
        If _skips IsNot Nothing Then
            Dim ordered = _skips.Rows.Cast(Of DataRow)().
                OrderBy(Function(s) If(IsDBNull(s("SkipOrder")), 99999, CInt(s("SkipOrder")))).
                ThenBy(Function(s) If(index.ContainsKey(Int(s, "QTo")), index(Int(s, "QTo")), Integer.MaxValue)).
                ThenBy(Function(s) If(index.ContainsKey(Int(s, "QFrom")), index(Int(s, "QFrom")), Integer.MaxValue))
            For Each s In ordered
                Dim qFrom = TopLevel(Int(s, "QFrom"))
                If qFrom Is Nothing OrElse Not pageLast.ContainsKey(QidOf(qFrom)) Then
                    Warn(Nothing, $"Skip rule {Int(s, "Skip_ID")} starts from a question that wasn't imported; skipped.") : Continue For
                End If
                If Str(s, "ExtData").Trim() <> "" Then
                    Warn(qFrom, "has a skip based on respondent profile data, which SurveyFlow can't do yet; not imported.") : Continue For
                End If
                Dim holderRow = pageLast(QidOf(qFrom))
                If Not _sfQ.ContainsKey(QidOf(holderRow)) Then Continue For
                Dim target = ResolveTarget(holderRow, Int(s, "QTo"), index)
                If target Is Nothing Then Continue For
                Dim conds = ParseRule(Str(s, "Rule"), qFrom)
                If conds Is Nothing Then Continue For
                AddRule(_sfQ(QidOf(holderRow)), conds, target)
            Next
        End If

        ' 3) Unconditional page jump (Question.Next_Question) — last, as a default.
        For Each q In _top
            Dim nq = Int(q, "Next_Question")
            If nq = 0 OrElse Not Bool(q, "Page_Break") OrElse Not _sfQ.ContainsKey(QidOf(q)) Then Continue For
            Dim target = ResolveTarget(q, nq, index)
            If target Is Nothing Then Continue For
            CType(_sfQ(QidOf(q))("branching_rules"), JArray).Add(New JObject From {{"answer_value", "__default__"}, {"go_to_question_id", target}})
        Next
    End Sub

    Private Sub AddRule(holder As JObject, conditions As JArray, target As String)
        CType(holder("branching_rules"), JArray).Add(New JObject From {
            {"conditions", conditions}, {"logic", "and"}, {"go_to_question_id", target}})
    End Sub

    ' -1 → end of survey; otherwise the target's SurveyFlow id. Backward or
    ' unknown targets are dropped (SurveyFlow only jumps forward).
    Private Function ResolveTarget(fromQ As DataRow, targetQid As Integer, index As Dictionary(Of Integer, Integer)) As String
        If targetQid = -1 Then Return "__end"
        Dim t = TopLevel(targetQid)
        If t Is Nothing OrElse Not index.ContainsKey(QidOf(t)) OrElse Not _sfQ.ContainsKey(QidOf(t)) Then
            Warn(fromQ, $"has a skip to a question that wasn't imported (PIQ {targetQid}); that skip was dropped.")
            Return Nothing
        End If
        If index(QidOf(t)) <= index(QidOf(fromQ)) Then
            Warn(fromQ, $"has a skip back to an earlier question (PIQ {targetQid}); SurveyFlow only skips forward, so it was dropped.")
            Return Nothing
        End If
        Return _sfId(QidOf(t))
    End Function

    ' "QID,RID;!QID,RID;" → SurveyFlow conditions (ANDed). Conditions PIQ
    ' would ignore (question not in the survey) are dropped, as PIQ treats
    ' them as true.
    Private Function ParseRule(rule As String, fromQ As DataRow) As JArray
        Dim conds As New JArray
        For Each raw In rule.Split(";"c)
            Dim part = raw.Trim()
            If part = "" Then Continue For
            Dim negate = part.StartsWith("!")
            If negate Then part = part.Substring(1)
            Dim bits = part.Split(","c)
            If bits.Length < 2 Then Continue For
            Dim qid = CInt(Val(bits(0))), rid = CInt(Val(bits(1)))
            If Not _qById.ContainsKey(qid) Then Continue For
            Dim srcQ = _qById(qid)
            Dim top = TopLevel(qid)
            If top Is Nothing OrElse Not _sfId.ContainsKey(QidOf(top)) Then Continue For
            If rid = -1 OrElse Not _respById.ContainsKey(rid) Then
                Warn(fromQ, "has a skip condition SurveyFlow can't express (blank answer); that rule was dropped.")
                Return Nothing
            End If
            Dim resp = _respById(rid)
            If {1, 5, 11, 15}.Contains(Int(resp, "Controls")) Then
                Warn(fromQ, "has a skip that depends on a text box being left blank; that rule was dropped.")
                Return Nothing
            End If
            Dim c As New JObject From {
                {"question_id", _sfId(QidOf(top))},
                {"answer_value", CleanText(Str(resp, "Response_Text"))}
            }
            If Not IsDBNull(srcQ("Parent_ID")) Then c("row") = CleanText(Str(srcQ, "QText"))   ' grid row
            If negate Then c("operator") = "not_equals"
            conds.Add(c)
        Next
        Return conds
    End Function

    ' ===================================================================
    ' Helpers
    ' ===================================================================
    Private Function TopLevel(qid As Integer) As DataRow
        If Not _qById.ContainsKey(qid) Then Return Nothing
        Dim q = _qById(qid)
        While Not IsDBNull(q("Parent_ID"))
            Dim p = CInt(q("Parent_ID"))
            If Not _qById.ContainsKey(p) Then Return Nothing
            q = _qById(p)
        End While
        Return If(_top.Contains(q), q, Nothing)
    End Function

    Private Function ChildRows(qid As Integer) As List(Of DataRow)
        Return If(_children.ContainsKey(qid), _children(qid), New List(Of DataRow))
    End Function

    Private Function Resps(qid As Integer) As List(Of DataRow)
        Return If(_respByQ.ContainsKey(qid), _respByQ(qid), New List(Of DataRow))
    End Function

    Private Function Sel(q As DataRow) As Integer
        If Not IsDBNull(q("sel")) Then Return CInt(q("sel"))
        ' NULL sel: grids default to radio, everything else to a text box.
        Return If(Int(q, "Question_Type") = 0 OrElse ChildRows(QidOf(q)).Count > 0, 6, 0)
    End Function

    Private Function IsHeading(q As DataRow) As Boolean
        Return Int(q, "Question_Type") = 3 OrElse (Not IsDBNull(q("sel")) AndAlso CInt(q("sel")) = 8)
    End Function

    Private Shared Function FormatPart(q As DataRow, i As Integer) As String
        Dim parts = Str(q, "Format").Split(","c)
        Return If(parts.Length > i, parts(i).Trim(), "")
    End Function

    Private Shared Function QidOf(q As DataRow) As Integer
        Return CInt(q("Question_ID"))
    End Function

    Private Shared Function Str(r As DataRow, col As String) As String
        If Not r.Table.Columns.Contains(col) OrElse IsDBNull(r(col)) Then Return ""
        Return System.Convert.ToString(r(col))
    End Function

    Private Shared Function Int(r As DataRow, col As String) As Integer
        If Not r.Table.Columns.Contains(col) OrElse IsDBNull(r(col)) Then Return 0
        Dim v As Double
        Return If(Double.TryParse(System.Convert.ToString(r(col)), v), CInt(v), 0)
    End Function

    Private Shared Function Bool(r As DataRow, col As String) As Boolean
        If Not r.Table.Columns.Contains(col) OrElse IsDBNull(r(col)) Then Return False
        Dim v = r(col)
        If TypeOf v Is Boolean Then Return CBool(v)
        Return Int(r, col) <> 0
    End Function

    ' PIQ HTML ignores line breaks; SurveyFlow shows them, so fold them to spaces.
    Private Shared Function CleanText(s As String) As String
        If String.IsNullOrEmpty(s) Then Return ""
        Return Regex.Replace(s, "[\r\n]+", " ").Replace(ChrW(&HA0), " ").Trim()
    End Function

    Private Shared Function Snippet(s As String) As String
        Dim t = Regex.Replace(If(s, ""), "<[^>]+>", "").Trim()
        Return If(t.Length > 60, t.Substring(0, 57) & "...", t)
    End Function

    Private Sub Warn(q As DataRow, message As String)
        If q Is Nothing Then
            _result.Warnings.Add(message)
        Else
            Dim n = If(_num.ContainsKey(QidOf(q)), "Q" & _num(QidOf(q)), "Text block")
            _result.Warnings.Add($"{n} (""{Snippet(Str(q, "QText"))}"") {message}")
        End If
    End Sub

End Class
