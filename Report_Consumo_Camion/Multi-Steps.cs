using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CamionReportGPT
{
    public partial class MainWindow
    {
        public record GptStep(int Number, string Sql, string? Python);
        public record StepResult(int Number, DataTable Table, PythonOutput? Py);

        #region ▶︎ Parsing multi-step
        private static List<GptStep> EstraiStepDaRisposta(string risposta)
        {
            var steps = new List<GptStep>();
            var pyMap = new Dictionary<int, string>();
            var pyRegex = new Regex("```python(?<code>.*?)```\\s*@3\\s*-\\s*(?<n>\\d+)", RegexOptions.Singleline);
            foreach (Match m in pyRegex.Matches(risposta))
            {
                int n = int.Parse(m.Groups["n"].Value);
                if (!pyMap.ContainsKey(n))
                    pyMap[n] = m.Groups["code"].Value.Trim();
            }

            var sqlRegex = new Regex("@(?<sql>.+?)@3\\s*-\\s*(?<n>\\d+)", RegexOptions.Singleline);
            foreach (Match m in sqlRegex.Matches(risposta))
            {
                int n = int.Parse(m.Groups["n"].Value);
                if (steps.Any(s => s.Number == n))
                    continue; // evita doppio parsing dello stesso step
                string sql = m.Groups["sql"].Value.Trim();
                pyMap.TryGetValue(n, out string? py);
                steps.Add(new GptStep(n, sql, py));
            }
            steps.Sort((a, b) => a.Number.CompareTo(b.Number));
            return steps;
        }
        #endregion

        #region ▶︎ Esecuzione multi-step
        private async Task<List<StepResult>> EseguiStepsAsync(IEnumerable<GptStep> steps)
        {
            var results = new List<StepResult>();
            var executed = new HashSet<int>();
            foreach (var step in steps.OrderBy(s => s.Number))
            {
                if (!executed.Add(step.Number))
                    continue; // evita doppia esecuzione

                DataTable table = await EseguiQueryAsync(step.Sql);
                PythonOutput? pyOut = null;
                if (!string.IsNullOrWhiteSpace(step.Python))
                {
                    string csv = Path.GetTempFileName();
                    SaveDataTableToCsv(table, csv, csvProgress);
                    pyOut = await EseguiPythonAsync(step.Python, csv);
                }
                results.Add(new StepResult(step.Number, table, pyOut));
            }
            return results;
        }
        #endregion

        #region ▶︎ Sintesi risultati
        private async Task<string> InviaAdAnalistaAsync(IEnumerable<StepResult> results)
        {
            var sb = new StringBuilder();
            foreach (var r in results.OrderBy(r => r.Number))
            {
                sb.AppendLine($"Step {r.Number}:");
                sb.AppendLine(DataTableToMarkdown(r.Table));
                if (r.Py != null)
                {
                    sb.AppendLine($"Risultato Python: {r.Py.Result}");
                    sb.AppendLine($"Formula: {r.Py.Formula}");
                    sb.AppendLine($"Spiegazione: {r.Py.Explain}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("Fornisci una breve sintesi.");
            return await assistantClient.RunAsync(AssistantId, sb.ToString());
        }
        #endregion
    }
}
