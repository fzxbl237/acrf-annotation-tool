using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace AcrfAnnotationTool.Services.Crf;

public enum CrfDatabaseType
{
    Rave,
    VieDoc,
    AuroraPrime,
    Taimei
}

public sealed class CrfExtractedAnnotation
{
    public string? FormName { get; set; }
    public int PageNum { get; set; }
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int OrderY { get; set; }
}

public static class CrfExtractionService
{
    public static IReadOnlyList<CrfExtractedAnnotation> Extract(
        string crfPath,
        CrfDatabaseType databaseType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crfPath);
        cancellationToken.ThrowIfCancellationRequested();
        return databaseType switch
        {
            CrfDatabaseType.Rave => RaveExtract(crfPath, cancellationToken),
            CrfDatabaseType.VieDoc => VieDocExtract(crfPath, cancellationToken),
            CrfDatabaseType.AuroraPrime => AuroraPrimeExtract(crfPath, cancellationToken),
            CrfDatabaseType.Taimei => TaimeiExtract(crfPath, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(databaseType), databaseType, null)
        };
    }

    private static List<CrfExtractedAnnotation> RaveExtract(string crfPath, CancellationToken cancellationToken)
    {
        List<CrfExtractedAnnotation> res = [];
        using var document = PdfDocument.Open(crfPath);
        string currentForm = string.Empty;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            //if(page.Number!=2) continue;
            List<TextBlock> contents = [];
            List<double> lines = [];
            List<double> boxes = [];
            foreach (var path in page.Paths)
            {
                var rectangle = path.GetBoundingRectangle();
                if (rectangle.HasValue)
                {
                    if(rectangle.Value.Left<300)
                        lines.Add(rectangle.Value.Top);
                    else if(rectangle.Value.Left>450)
                    {
                        boxes.Add(rectangle.Value.Top);
                    }
                }
            }

            var letters = page.Letters; // no preprocessing

            // 1. Extract words
            var wordExtractor = NearestNeighbourWordExtractor.Instance;

            var words = wordExtractor.GetWords(letters);
                

            // 2. Segment page
            //var pageSegmenter = DocstrumBoundingBoxes.Instance;
            var pageSegmenter = MyDocstrumBoundingBoxes.Instance;

            //var pageSegmenter = new DocstrumBoundingBoxes(new DocstrumBoundingBoxes.DocstrumBoundingBoxesOptions(){WithinLineMultiplier = 0.5});
            
            var textBlocks = pageSegmenter.GetBlocks(words);
            List<TextLine> allLines = [];
            foreach (var textBlock in textBlocks)
            {
                allLines.AddRange(textBlock.TextLines);
            }
            if(lines.Count ==0)
                continue;
            var max = lines.Max();
            var min = lines.Min();
            //header
            var headerLines = allLines.Where(o=>o.BoundingBox.Top>=max && o.Text.StartsWith("Form")).ToList();
            if(headerLines.Count > 0)
                contents.Add(new TextBlock(headerLines," "));
                
            //variable
            var orderLines = lines.OrderDescending().ToList();
            for (var i = 0; i < orderLines.Count; i++)
            {
                if (i != orderLines.Count - 1)
                {
                    var upper = orderLines[i];
                    var lower = orderLines[i + 1];
                    var textLines = allLines.Where(o=>o.BoundingBox.Top<=upper && o.BoundingBox.Top>=lower && o.BoundingBox.Left<300).ToList();
                    if(textLines.Count > 0)
                        contents.Add(new TextBlock(textLines," "));
                    var boxLines = boxes.Where(o=>o<=upper&& o>=lower).OrderDescending().ToList();
                    var valueLines = allLines.Where(o=>o.BoundingBox.Top<=upper && o.BoundingBox.Top>=lower && o.BoundingBox.Left>=300).ToList();
                    if(boxLines.Count==0 && valueLines.Count>0)
                        contents.Add(new TextBlock(valueLines," "));
                    else
                    {
                        for (var j = 0; j < boxLines.Count; j++)
                        {
                            var boxUpper=boxLines[j];
                            if (j != boxLines.Count - 1)
                            {
                                var boxLower=boxLines[j+1];
                                var eachLines = valueLines.Where(o=>(int)o.BoundingBox.Top<=(int)boxUpper && (int)o.BoundingBox.Top>(int)boxLower ).ToList();
                                if(eachLines.Count>0)
                                    contents.Add(new TextBlock(eachLines," "));
                            }
                            else
                            {
                                var eachLines = valueLines.Where(o=>(int)o.BoundingBox.Top<=(int)boxUpper).ToList();
                                if(eachLines.Count>0)
                                    contents.Add(new TextBlock(eachLines," "));
                            }
                        }
                    }
                }
            }
                
                
            // 3. Postprocessing
            var readingOrder = UnsupervisedReadingOrderDetector.Instance;
            var orderedTextBlocks = readingOrder.Get(contents);

            // 4. Add debug info - Bounding boxes and reading order
            foreach (var block in orderedTextBlocks)
            {
                var annotation = new CrfExtractedAnnotation();
                if (block.Text.StartsWith("Form:"))
                {
                    currentForm = block.Text.Replace("Form:", "");
                    annotation.Type = "Header";
                }
                else
                {
                    annotation.Type=block.BoundingBox.Left<300?"Variable":"Value";
                }
                annotation.FormName = currentForm;
                annotation.PageNum = page.Number;
                annotation.Text = block.Text;
                annotation.X=(int)block.BoundingBox.Right;
                annotation.Y=(int)block.BoundingBox.Bottom;
                res.Add(annotation);
            }
                
        }
        
        string? currentHeader = string.Empty;
        string? currentVariable = string.Empty;
        foreach (var annotation in res)
        {
            if (annotation.Type == "Header")
            {
                currentHeader = annotation.Text;
            }else if (annotation.Type == "Variable")
            {
                currentVariable = annotation.Text;
            }
            annotation.FormName = currentHeader;
            if (annotation.Type != "Header")
            {
                annotation.Variable =currentVariable;
            }
            if (annotation.Type == "Value")
            {
                annotation.Value = annotation.Text;
            }
        }

        return res;
    }

    private static List<CrfExtractedAnnotation> VieDocExtract(string crfPath, CancellationToken cancellationToken)
    {
        List<CrfExtractedAnnotation> res = [];
        string currentType = string.Empty;
        using (var document = PdfDocument.Open(crfPath))
        {
            string currentForm = string.Empty;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                //if(page.Number!=1) continue;
                var letters = page.Letters;

                var header = letters.FirstOrDefault(o => Math.Abs(o.FontSize - 24) < 0.001);
                // var codeListLetter = letters.FirstOrDefault(o => o.FontName != null && o.FontName.Contains("Bold") && Math.Abs(o.FontSize - 12) < 0.001);
                var codeListLetter = letters.FirstOrDefault(o => o.FontName != null && o.FontName.Contains("Bold") && o.FontSize>16);
                double high = 1000;
                double low = 35;
                if (header != null)
                {
                    high = header.Location.Y;
                    currentType = "Header";
                }

                if (codeListLetter != null)
                {
                    low = codeListLetter.Location.Y;
                    currentType = "CodeList";
                }

                if (header != null || codeListLetter != null || currentType == "Header")
                {
                    var filteredLetters
                        = letters.Where(o=>high-o.Location.Y>-0.0001 && o.Location.Y-low>1).ToList();
                    var variableLetters
                        = filteredLetters.Where(o=>Math.Abs(o.FontSize - 16) < 0.001).ToList();
                    List<TextBlock> list = [];
                    var variableBlocks = GetTextBlocks(variableLetters);
                    
                    var headerLetters
                        = filteredLetters.Where(o=>Math.Abs(o.FontSize - 24) < 0.001).ToList();
                    var headerBlocks = GetTextBlocks(headerLetters);
                    
                    var valueLetters
                        = filteredLetters.Where(o=>(Math.Abs(o.FontSize - 14) < 0.001)|| (Math.Abs(o.FontSize - 15) < 0.001)).ToList();
                    var valueBlocks = GetTextBlocks(valueLetters);
                    list.AddRange(variableBlocks);
                    list.AddRange(headerBlocks);
                    list.AddRange(valueBlocks);

                    foreach (var textBlock in list)
                    {
                        var annotation = new CrfExtractedAnnotation();
                        annotation.PageNum = page.Number;
                        if (Math.Abs(GetFontSize(textBlock) - 24) < 0.001)
                        {
                            annotation.Type = "Header";
                        }else if (Math.Abs(GetFontSize(textBlock) - 16) < 0.001)
                        {
                            annotation.Type = "Variable";
                        }
                        else
                        {
                            annotation.Type = "Value";
                        }
                        annotation.Text = textBlock.Text.Replace(System.Environment.NewLine," ");
                        annotation.X = (int)textBlock.BoundingBox.Right;
                        annotation.Y = (int)textBlock.BoundingBox.Top;
                        if(annotation.Type == "Value")
                        {
                            annotation.OrderY = (int)(textBlock.BoundingBox.Bottom - 3);
                        }else
                        {
                            annotation.OrderY = (int)textBlock.BoundingBox.Bottom;
                        }
                        res.Add(annotation);
                    }
                    
                    
                }
            }
        }

        var annotations = res.OrderBy(o=>o.PageNum).ThenByDescending(o=>o.OrderY).ThenBy(o=>o.X).ToList();
        string? currentHeader = string.Empty;
        string? currentVariable = string.Empty;
        foreach (var annotation in annotations)
        {
            if (annotation.Type == "Header")
            {
                currentHeader = annotation.Text;
            }else if (annotation.Type == "Variable")
            {
                currentVariable = annotation.Text;
            }
            annotation.FormName = currentHeader;
            if (annotation.Type != "Header")
            {
                annotation.Variable =currentVariable;
            }
            if (annotation.Type == "Value")
            {
                annotation.Value = annotation.Text;
            }
        }

        return annotations;
    }
    
    private static List<CrfExtractedAnnotation> AuroraPrimeExtract(string crfPath, CancellationToken cancellationToken)
    {
        List<CrfExtractedAnnotation> res = [];
        string currentType = string.Empty;
        using (var document = PdfDocument.Open(crfPath))
        {
            string currentForm = string.Empty;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readOnlyList = page.Paths;
                HashSet<double> doubles = [];
                HashSet<int> boxDoubles = [];
                foreach (var pdfPath in readOnlyList)
                {
                    var boundingRectangle = pdfPath.GetBoundingRectangle();

                    if (boundingRectangle is { Height: < 0.55, Left: < 60 })
                    {
                        doubles.Add(boundingRectangle.Value.Top);
                    }
                    else if (boundingRectangle is { Height: > 1, Left: > 60, Width: > 1 })
                    {
                        boxDoubles.Add((int)boundingRectangle.Value.Top);
                    }

                }
                

                var lines = doubles.ToList().OrderDescending().ToList();
                if(lines.Count==0) continue;
                lines.Insert(0, 1000);

                var boxes = boxDoubles.ToList().OrderDescending().ToList();


                var letters = page.Letters; // no preprocessing
                var letters1 = letters.Where(o => o.FontSize > 18).ToList();
                var wordExtractor = NearestNeighbourWordExtractor.Instance;

                var words = wordExtractor.GetWords(letters1).ToList();
                var pageSegmenter = DocstrumBoundingBoxes.Instance;
                var readingOrder = UnsupervisedReadingOrderDetector.Instance;

                for (var i = 0; i < lines.Count; i++)
                {
                    if (i != lines.Count - 1)
                    {
                        var upper = lines[i];
                        var lower = lines[i + 1];
                        var list = words.Where(o => o.BoundingBox.Top <= upper && o.BoundingBox.Bottom >= lower)
                            .ToList();

                        var lefts = list.Where(o => o.BoundingBox.Left < 225).ToList();
                        var rights = list.Where(o => o.BoundingBox.Left >= 225).ToList();
                        var leftTextBlocks = pageSegmenter.GetBlocks(lefts);
                        var orderedLeftTextBlocks = readingOrder.Get(leftTextBlocks);

                        foreach (var orderedTextBlock in orderedLeftTextBlocks)
                        {
                            if (orderedTextBlock.BoundingBox.Top > 775) continue;
                            var replace = Regex.Replace(orderedTextBlock.Text, @"[\r\n]+", " ");
                            if (i == 0)
                            {
                                currentForm = replace;
                            }
                            var annotation = new CrfExtractedAnnotation
                            {
                                PageNum = page.Number,
                                FormName = currentForm,
                                Type = i == 0 ? "Header" : "Variable",
                                Text = replace,
                                X = (int)(orderedTextBlock.BoundingBox.Right),
                                Y = (int)(orderedTextBlock.BoundingBox.Top)
                            };
                            res.Add(annotation);
                        }

                        var ints = boxes.Where(o => o <= upper && o >= lower).ToList();
                        ints.Add(0);
                        for (var j = 0; j < ints.Count; j++)
                        {
                            if (j != ints.Count - 1)
                            {
                                var boxUpper = ints[j] + 5;
                                var boxLower = ints[j + 1] + 5;
                                var valueWords = rights
                                    .Where(o => o.BoundingBox.Top <= boxUpper && o.BoundingBox.Bottom >= boxLower)
                                    .ToList();
                                var textBlocks = pageSegmenter.GetBlocks(valueWords);
                                List<TextLine> textLines = [];
                                foreach (var textBlock in textBlocks)
                                {
                                    textLines.AddRange(textBlock.TextLines);
                                }

                                if (textLines.Count <= 0) continue;
                                var block = new TextBlock(textLines);
                                var annotation = new CrfExtractedAnnotation
                                {
                                    PageNum = page.Number,
                                    FormName = currentForm,
                                    Type = "Value",
                                    Text = block.Text.Replace(Environment.NewLine, " "),
                                    X = (int)(block.BoundingBox.Right),
                                    Y = (int)(block.BoundingBox.Top)
                                };
                                res.Add(annotation);

                            }
                        }
                    }
                }
            }
        }
        
        string? currentHeader = string.Empty;
        string? currentVariable = string.Empty;
        foreach (var annotation in res)
        {
            if (annotation.Type == "Header")
            {
                currentHeader = annotation.Text;
            }else if (annotation.Type == "Variable")
            {
                currentVariable = annotation.Text;
            }
            annotation.FormName = currentHeader;
            if (annotation.Type != "Header")
            {
                annotation.Variable =currentVariable;
            }
            if (annotation.Type == "Value")
            {
                annotation.Value = annotation.Text;
            }
        }
        return res;
    }
    
    private static List<CrfExtractedAnnotation> TaimeiExtract(string crfPath, CancellationToken cancellationToken)
    {
        List<CrfExtractedAnnotation> res = [];
        using var document = PdfDocument.Open(crfPath);

        var pages = new List<(int Number, List<TaimeiLetter> Letters)>();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<TaimeiLetter> letters = [];
            foreach (var letter in page.Letters)
            {
                var box = letter.BoundingBox;
                letters.Add(new TaimeiLetter(letter.Value, box.Left, box.Right, box.Top,
                    letter.StartBaseLine.Y, Math.Round(letter.FontSize, 1)));
            }
            pages.Add((page.Number, letters));
        }

        if (pages.Count == 0)
            return res;

        var contentLetters = pages.SelectMany(p => p.Letters)
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .ToList();
        if (contentLetters.Count == 0)
            return res;

        var bodySize = contentLetters
            .GroupBy(l => l.Size)
            .OrderByDescending(g => g.Count())
            .First().Key;
        var titleSizes = contentLetters
            .Where(l => l.Size > bodySize)
            .GroupBy(l => l.Size)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (titleSizes.Count == 0)
            return res;
        var titleSize = titleSizes[0].Key;

        var forms = new List<TaimeiForm>();
        foreach (var (number, letters) in pages)
        {
            var titleLetters = letters.Where(l => Math.Abs(l.Size - titleSize) < 0.05).ToList();
            if (titleLetters.Count == 0)
                continue;
            var bodyLetters = letters.Where(l => Math.Abs(l.Size - bodySize) < 0.05).ToList();
            if (bodyLetters.Count == 0)
                continue;

            var titleBaseline = titleLetters.Max(l => l.Baseline);
            var titleLine = titleLetters.Where(l => Math.Abs(l.Baseline - titleBaseline) <= 2)
                .OrderBy(l => l.Left).ToList();
            var title = string.Concat(titleLine.Select(l => l.Text)).Trim();

            var ordered = bodyLetters.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left).ToList();
            List<TaimeiRow> rows = [];
            foreach (var letter in ordered)
            {
                if (rows.Count > 0 && Math.Abs(rows[^1].Baseline - letter.Baseline) <= 2)
                    rows[^1].Letters.Add(letter);
                else
                    rows.Add(new TaimeiRow(letter.Baseline, [letter]));
            }
            rows.RemoveAll(r => r.Letters.All(l => string.IsNullOrWhiteSpace(l.Text)));

            forms.Add(new TaimeiForm(number, title, titleLine.Max(l => l.Top), titleLine.Max(l => l.Right), rows));
        }

        if (forms.Count == 0)
            return res;

        var questionThreshold = GetTaimeiQuestionThreshold(forms);
        var labelBoundary = GetTaimeiLabelBoundary(forms);

        foreach (var form in forms)
        {
            res.Add(new CrfExtractedAnnotation
            {
                PageNum = form.Number,
                Type = "Header",
                Text = form.Title,
                X = (int)form.TitleRight,
                Y = (int)form.TitleTop
            });

            List<string> labelParts = [];
            List<List<TaimeiLetter>> valueLines = [];
            double labelTop = double.MinValue, labelRight = 0;
            var questionIndex = 0;

            void Flush()
            {
                var labelText = JoinTaimeiParts(labelParts);
                var valueChunks = GetTaimeiValueChunks(valueLines);
                if (labelText.Length > 0)
                {
                    res.Add(new CrfExtractedAnnotation
                    {
                        PageNum = form.Number,
                        Type = "Variable",
                        Text = labelText,
                        X = (int)labelRight,
                        Y = (int)labelTop
                    });
                    foreach (var chunk in valueChunks)
                    {
                        res.Add(new CrfExtractedAnnotation
                        {
                            PageNum = form.Number,
                            Type = "Value",
                            Text = chunk.Text,
                            X = (int)chunk.Right,
                            Y = (int)chunk.Top
                        });
                    }
                }
                else if (valueChunks.Count > 0)
                {
                    var previous = res.LastOrDefault(a => a.Type != "Header");
                    if (questionIndex == 0 && valueChunks.Count == 1 && !valueChunks[0].IsOption
                        && previous is { Type: "Value" } previousValue)
                    {
                        previousValue.Text = JoinTaimeiParts([previousValue.Text ?? string.Empty, valueChunks[0].Text]);
                    }
                    else
                    {
                        foreach (var chunk in valueChunks)
                        {
                            res.Add(new CrfExtractedAnnotation
                            {
                                PageNum = form.Number,
                                Type = "Value",
                                Text = chunk.Text,
                                X = (int)chunk.Right,
                                Y = (int)chunk.Top
                            });
                        }
                    }
                }
                questionIndex++;
                labelParts.Clear();
                valueLines.Clear();
                labelTop = double.MinValue;
                labelRight = 0;
            }

            double? previousBaseline = null;
            foreach (var row in form.Rows)
            {
                if (previousBaseline.HasValue && previousBaseline.Value - row.Baseline > questionThreshold)
                    Flush();
                previousBaseline = row.Baseline;

                var labelLetters = row.Letters.Where(l => l.Left < labelBoundary)
                    .OrderBy(l => l.Left).ToList();
                var valueLetters = row.Letters.Where(l => l.Left >= labelBoundary)
                    .OrderBy(l => l.Left).ToList();
                var labelText = string.Concat(labelLetters.Select(l => l.Text)).Trim();
                var valueText = string.Concat(valueLetters.Select(l => l.Text)).Trim();

                if (labelText.Length > 0)
                {
                    labelParts.Add(labelText);
                    labelTop = Math.Max(labelTop, labelLetters.Max(l => l.Top));
                    labelRight = Math.Max(labelRight, labelLetters.Max(l => l.Right));
                }
                if (valueText.Length > 0)
                    valueLines.Add(valueLetters);
            }
            Flush();
        }

        string? currentHeader = string.Empty;
        string? currentVariable = string.Empty;
        foreach (var annotation in res)
        {
            if (annotation.Type == "Header")
            {
                currentHeader = annotation.Text;
            }
            else if (annotation.Type == "Variable")
            {
                currentVariable = annotation.Text;
            }
            annotation.FormName = currentHeader;
            if (annotation.Type != "Header")
            {
                annotation.Variable = currentVariable;
            }
            if (annotation.Type == "Value")
            {
                annotation.Value = annotation.Text;
            }
        }

        return res;
    }

    private sealed record TaimeiLetter(string Text, double Left, double Right, double Top, double Baseline, double Size);

    private sealed record TaimeiRow(double Baseline, List<TaimeiLetter> Letters);

    private sealed record TaimeiForm(int Number, string Title, double TitleTop, double TitleRight, List<TaimeiRow> Rows);

    private sealed record TaimeiValueChunk(string Text, double Top, double Right, bool IsOption);

    private static double GetTaimeiQuestionThreshold(List<TaimeiForm> forms)
    {
        List<double> gaps = [];
        foreach (var form in forms)
        {
            for (var i = 1; i < form.Rows.Count; i++)
            {
                var gap = form.Rows[i - 1].Baseline - form.Rows[i].Baseline;
                if (gap > 0.5 && gap < 60)
                    gaps.Add(Math.Round(gap, 1));
            }
        }

        if (gaps.Count == 0)
            return 20;

        List<(double Start, double End, double Sum, int Count)> clusters = [];
        foreach (var gap in gaps.OrderBy(g => g))
        {
            if (clusters.Count > 0 && gap - clusters[^1].Start <= 1)
            {
                var last = clusters[^1];
                clusters[^1] = (last.Start, gap, last.Sum + gap, last.Count + 1);
            }
            else
            {
                clusters.Add((gap, gap, gap, 1));
            }
        }

        var significant = clusters.Where(c => c.Count >= Math.Max(10, gaps.Count / 100)).ToList();
        // The two smallest row gaps are the in-question line pitch and the gap between questions.
        if (significant.Count >= 2)
            return (significant[0].Sum / significant[0].Count + significant[1].Sum / significant[1].Count) / 2;

        var pitchCluster = clusters.OrderByDescending(c => c.Count).ThenBy(c => c.Start).First();
        var pitch = pitchCluster.Sum / pitchCluster.Count;
        var nextCluster = clusters.Where(c => c.Start > pitch + 0.5).OrderBy(c => c.Start).FirstOrDefault();
        return nextCluster.Count > 0 ? (pitch + nextCluster.Sum / nextCluster.Count) / 2 : pitch * 1.15;
    }

    private static double GetTaimeiLabelBoundary(List<TaimeiForm> forms)
    {
        List<double> starts = [];
        foreach (var form in forms)
        {
            foreach (var row in form.Rows)
            {
                var visible = row.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
                if (visible.Count > 0)
                    starts.Add(visible.Min(l => l.Left));
            }
        }

        if (starts.Count == 0)
            return double.MaxValue;

        List<(double Start, double End, int Count)> clusters = [];
        foreach (var start in starts.OrderBy(s => s))
        {
            if (clusters.Count > 0 && start - clusters[^1].End <= 2)
            {
                var last = clusters[^1];
                clusters[^1] = (last.Start, start, last.Count + 1);
            }
            else
            {
                clusters.Add((start, start, 1));
            }
        }

        var significant = clusters.Where(c => c.Count >= Math.Max(10, starts.Count / 100)).ToList();
        if (significant.Count < 2)
            return significant.Count > 0 ? significant[0].End + 2 : double.MaxValue;

        // The second column start is measured from line-first glyphs; cell content can begin
        // slightly left of it, so keep the label boundary one clustering tolerance below.
        return significant[1].Start - 2;
    }

    private static string JoinTaimeiParts(IEnumerable<string> parts)
    {
        var result = string.Empty;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            if (result.Length == 0)
            {
                result = part;
            }
            else if (IsTaimeiAlnum(result[^1]) && IsTaimeiAlnum(part[0]))
            {
                result += " " + part;
            }
            else
            {
                result += part;
            }
        }
        return result;
    }

    private static bool IsTaimeiAlnum(char c) => c < 128 && char.IsLetterOrDigit(c);

    private static List<TaimeiValueChunk> GetTaimeiValueChunks(List<List<TaimeiLetter>> lines)
    {
        List<TaimeiValueChunk> chunks = [];
        List<(int Line, TaimeiLetter Letter)> letters = [];
        var isOption = false;

        void Close()
        {
            if (letters.Count == 0)
                return;
            List<string> parts = [];
            foreach (var group in letters.GroupBy(x => x.Line))
                parts.Add(string.Concat(group.Select(x => x.Letter.Text)));
            var text = StripTaimeiPlaceholder(JoinTaimeiParts(parts));
            if (text.Length > 0)
            {
                chunks.Add(new TaimeiValueChunk(text,
                    letters.Max(x => x.Letter.Top),
                    letters.Max(x => x.Letter.Right),
                    isOption));
            }
            letters.Clear();
        }

        for (var line = 0; line < lines.Count; line++)
        {
            foreach (var letter in lines[line])
            {
                if (IsTaimeiOptionMarker(letter.Text))
                {
                    Close();
                    isOption = true;
                    continue;
                }
                letters.Add((line, letter));
            }
        }
        Close();
        return chunks;
    }

    private static bool IsTaimeiOptionMarker(string text) => text is "○" or "●" or "◯" or "〇";

    private static string StripTaimeiPlaceholder(string text)
    {
        // A repeated underscore run is a fill-in placeholder; a single underscore is literal text.
        List<char> chars = [];
        var run = 0;
        foreach (var c in text)
        {
            if (c == '_')
            {
                run++;
                continue;
            }
            if (run == 1)
                chars.Add('_');
            run = 0;
            chars.Add(c);
        }
        if (run == 1)
            chars.Add('_');
        return new string(chars.ToArray()).Trim();
    }

    private static double GetFontSize(TextBlock textBlock)
    {
        List<Letter> list = [];
        foreach (var textBlockTextLine in textBlock.TextLines)
        {
            foreach (var word in textBlockTextLine.Words)
            {
                list.AddRange(word.Letters);
            }
        }

        var max = list.Max(o=>o.FontSize);
        return max;
    }

    private static List<TextBlock> GetTextBlocks(List<Letter> letters)
    {
        var wordExtractor = NearestNeighbourWordExtractor.Instance;

        var words = wordExtractor.GetWords(letters);
        
        var pageSegmenter = DocstrumBoundingBoxes.Instance;
        
        var textBlocks = pageSegmenter.GetBlocks(words);

        List<TextLine> textLines = [];
        foreach (var textBlock in textBlocks)
        {
            var textBlockTextLines = textBlock.TextLines;
            List<TextLine> filteredTextLines = [];
            foreach (var textLine in GetTextLines(textBlockTextLines))
            {
                if (!Regex.IsMatch(textLine.Text, @"^#\d+$"))
                {
                    filteredTextLines.Add(textLine);
                }
            }
            textLines.AddRange(filteredTextLines);
        }

        var lines = textLines.OrderByDescending(o=>o.BoundingBox.Bottom).ThenBy(o=>o.BoundingBox.Left)
            .ToList();

        List<TextBlock> res = [];
        List<TextLine> currentLine = [];
        TextLine? previous = null;
        foreach (var textLine in lines)
        {
            if (previous != null && ((Math.Abs(textLine.BoundingBox.Bottom - previous.BoundingBox.Bottom) >= 12.1)
                                     || (Math.Abs(textLine.BoundingBox.Left - previous.BoundingBox.Right) >= 18.1 
                                         && (Math.Abs(textLine.BoundingBox.Bottom - previous.BoundingBox.Bottom) <2)))
               )
            {
                
                res.Add(new TextBlock(currentLine));
                currentLine.Clear();
            }
            currentLine.Add(textLine);
            previous = textLine;
        }

        if (currentLine.Any())
        {
            res.Add(new TextBlock(currentLine));
        }
        
        return res;
    }

    private static List<TextLine> GetTextLines(IReadOnlyList<TextLine> textLines)
    {
        List<TextLine> res = [];
        foreach (var textLine in textLines)
        {
            List<Word> currentWords = [];
            Word? previous = null;
            var words = textLine.Words.OrderByDescending(o=>o.BoundingBox.Bottom).ThenBy(o=>o.BoundingBox.Left).ToList();
            foreach (var textLineWord in words)
            {
                if (previous != null && !(Math.Abs(textLineWord.BoundingBox.Left - previous.BoundingBox.Right) < 18.1))
                {
                
                    res.Add(new TextLine(currentWords));
                    currentWords.Clear();
                }
                currentWords.Add(textLineWord);
                previous = textLineWord;
            }
            if (currentWords.Any())
            {
                res.Add(new TextLine(currentWords));
            }
            
        }
        return res;
    }
}