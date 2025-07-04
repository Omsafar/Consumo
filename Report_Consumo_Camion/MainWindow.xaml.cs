// MainWindow.xaml.cs – versione con RAG, feedback utente e salvataggio embedding
// 20 Giugno 2025

using CamionReportGPT.Vector;     // namespace dove hai messo HnswIndexService
using CsvHelper;
using HNSW.Net;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using static System.Runtime.InteropServices.JavaScript.JSType;
namespace CamionReportGPT
{
    public partial class MainWindow : Window
    {
        #region ✔︎ Costanti di configurazione
        public const string ConnString = "Server=192.168.1.24\\sgam;Database=PARATORI;User Id=sapara;Password=S@p4ra;Encrypt=True;TrustServerCertificate=True;";
        public const string OpenAIApiKey = "key";
        public const string EmbModel = "text-embedding-3-small"; // modello embedding
        private const string AssistantId = "asst_JMGFXRnQZv4mz4cOim6lDnXe"; // assistant per testo esplicativo
        private const string ErrorCorrectorAssistantId = "asst_ANQJ6yo179ZJXEtCZ3xuymSJ";

        #endregion

        #region ✔︎ Schema invio a GPT
        private const string SchemaDescrizione = @"Tabella: tbDatiConsumo
Campi:
- ID (int, PK)
- Data (date)
- Targa (varchar 10)
- Numero_Interno (varchar 10)
- Km_Totali (int)
- Litri_Totali (decimal)
- [Consumo_km/l] (decimal)
- Ore_Guida (decimal)
- Ore_Lavoro (decimal)
- Ore_Disp (decimal)
- Ore_Riposo (decimal)";
        #endregion

        #region ✔︎ Stato runtime
        private readonly ObservableCollection<MessaggioChat> messaggi = new();
        private static readonly HttpClient httpClient = new();
        private readonly OpenAIAssistantClient assistantClient;
        private readonly IProgress<(int current, int total)> csvProgress;
        private readonly HnswIndexService vecIdx;
        private string? UltimaDomandaUtente;
        private string? UltimaQuerySql;
        private string? UltimoPythonCode;
        private bool correctionAttempted;
        #endregion


        private static readonly string UtenteCorrente = Environment.UserName;

#if DEBUG
        private static void DebugMsg(string step, string msg) =>
    MessageBox.Show(msg, $"DEBUG – {step}");
#endif

        #region ▶︎ Costruttore
        public MainWindow()
        {
            InitializeComponent();
            assistantClient = new OpenAIAssistantClient(OpenAIApiKey);
            vecIdx = new HnswIndexService(@"C:\Users\omar.tagliabue\Desktop\RAG\rag.hnsw");
            chatPanel.ItemsSource = messaggi;
            csvProgress = new Progress<(int current, int total)>(p =>
            {
                progressBar.Maximum = p.total;
                progressBar.Value = p.current;
                loadingText.Text = $"Processato {p.current} di {p.total}";
            });
        }
        #endregion

        #region ▶︎ UI handlers (Invia / Enter / Feedback 👍)
        private async void btnInvia_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtInput.Text)) return;

            string domanda = txtInput.Text.Trim();
            txtInput.Clear();

            btnInvia.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            loadingText.Visibility = Visibility.Visible;
            progressBar.Value = 0;
            progressBar.IsIndeterminate = true;
            loadingText.Text = "Esecuzione query...";

            try
            {
                await GestioneConversazioneAsync(domanda);
            }
            catch (Exception ex)
            {
                Logger.Log(ex);
                MessageBox.Show($"Errore: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                loadingText.Visibility = Visibility.Collapsed;
                progressBar.IsIndeterminate = false;
                btnInvia.IsEnabled = true;
            }
        }

        private void txtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                btnInvia_Click(sender, e);
                e.Handled = true;
            }
        }

        /// <summary>
        /// L’utente conferma che la risposta è corretta (“✅ Utile”).
        /// Salviamo in DB + inseriamo nell’indice HNSW.
        /// </summary>
        private async void btnFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (UltimaDomandaUtente is null || UltimaQuerySql is null)
                {
                    MessageBox.Show("Nessuna risposta da validare.");
                    return;
                }

                // 1) Costruisco il testo esplicativo (assistant dedicato)
                string testoEsplicativo = await GeneraTestoEmbeddingAsync(
                    $"{UltimaDomandaUtente}\nSQL:\n{UltimaQuerySql}");

                // 2) Embedding + normalizzazione
                float[] vec = (await EmbeddingService.GetEmbeddingAsync(testoEsplicativo))
                              .ToArray();
                float[] vecNorm = HnswIndexService.Normalize(vec);

                // 3) Salva su SQL e ottieni l’ID generato
                int nuovoId = await RAGService.SalvaAsync(
                    UltimaDomandaUtente, UltimaQuerySql,
                    testoEsplicativo, JsonConvert.SerializeObject(vec), UtenteCorrente,
                    UltimoPythonCode);

                // 4) Inserisci nell’indice HNSW
                vecIdx.Add(nuovoId.ToString(), vecNorm);

                vecIdx.Save();
#if DEBUG
                DebugMsg("RAG Save", $"Nuovo ID={nuovoId}, vettore inserito e indice serializzato.");
#endif

                MessageBox.Show("✅ Salvato con successo in memoria RAG.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore nel salvataggio RAG: {ex.Message}");
            }
        }

        #endregion
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            vecIdx.Save();
            base.OnClosing(e);
        }
        #region ▶︎ Gestione conversazione
        /// <summary>
        /// Flusso principale ogni volta che l’utente invia una nuova domanda.
        /// </summary>
        private async Task GestioneConversazioneAsync(string domandaUtente)
        {
            // 0) Aggiungo il messaggio in chat (lato UI)
            messaggi.Add(new MessaggioChat { Testo = domandaUtente, IsUtente = true });

            // 1) --- Tentativo di recupero da memoria RAG --------------------------
            float[] vecQuery = HnswIndexService.Normalize(
                                   (await EmbeddingService.GetEmbeddingAsync(domandaUtente))
                                   .ToArray());

            var hits = vecIdx.Search(vecQuery, k: 3);

#if DEBUG
            DebugMsg("Similarity",
                     hits.Count == 0
                         ? "Nessun hit."
                         : $"Top score={hits[0].Score:F3} (soglia 0.70) → {(hits[0].Score > 0.70f ? "OK" : "KO")}");
#endif


            if (hits.Count > 0 && hits[0].Score > 0.70f)          // soglia empirica
            {
                int bestId = hits.Count > 0 ? hits[0].Id : -1;
                var row = await RAGService.GetByIdAsync(bestId);   // Domanda, QuerySql, Testo
                if (row != null)
                {
                    DataTable dt = await EseguiQueryAsync(row.QuerySql);
                    string md = DataTableToMarkdown(dt);

                    messaggi.Add(new MessaggioChat
                    {
                        Testo = $"**[MEMORIA RAG]**\n\n{md}\n\n_{row.Testo}_",
                        IsUtente = false
                    });
                    return;                                        // Fine flusso
                }
            }
            // ---------------------------------------------------------------------

            // 2) --- Chiedi a GPT di generare la SQL ------------------------------
            string initialPrompt = BuildInitialPrompt(domandaUtente);
            string gptReply = await CallGPTAsync(initialPrompt);

            if (Regex.IsMatch(gptReply, "@3\\s*-\\s*\\d+"))
            {
                var steps = EstraiStepDaRisposta(gptReply);
                var results = await EseguiStepsAsync(steps);

                progressBar.IsIndeterminate = false;
                progressBar.Value = progressBar.Maximum;
                loadingText.Text = "Completato";

                foreach (var r in results.OrderBy(r => r.Number))
                {
                    string md = DataTableToMarkdown(r.Table);
                    messaggi.Add(new MessaggioChat { Testo = $"**Step {r.Number}:**\\n\\n{md}", IsUtente = false });
                    if (r.Py != null)
                        messaggi.Add(new MessaggioChat { IsUtente = false, Result = r.Py.Result, Formula = r.Py.Formula, Explain = r.Py.Explain });
                }

                string sintesi = await InviaAdAnalistaAsync(results);
                messaggi.Add(new MessaggioChat { Testo = sintesi, IsUtente = false });

                UltimaDomandaUtente = domandaUtente;
                UltimaQuerySql = string.Join("\n\n", steps.Select(s => s.Sql));
                UltimoPythonCode = string.Join("\n\n", steps.Where(s => !string.IsNullOrWhiteSpace(s.Python)).Select(s => s.Python!));

                return;
            }

            correctionAttempted = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var (sql, flag, py) = EstraiSqlPythonEFlag(gptReply);

                    DataTable result = await EseguiQueryAsync(sql);
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 0;
                    loadingText.Text = "Elaborazione python...";
                    string tableMd = DataTableToMarkdown(result);
                    PythonOutput? pyOut = null;
                    if (!string.IsNullOrWhiteSpace(py))
                    {
                        string csv = Path.GetTempFileName();
                        SaveDataTableToCsv(result, csv, csvProgress);
                        pyOut = await EseguiPythonAsync(py, csv);
                    }

                    if (flag == 1)
                    {
                        string spiegazione = await OttieniSpiegazioneDaGPT(domandaUtente, sql, result);
                        messaggi.Add(new MessaggioChat { Testo = tableMd, IsUtente = false });
                        messaggi.Add(new MessaggioChat { Testo = spiegazione, IsUtente = false });
                    }
                    else
                    {
                        messaggi.Add(new MessaggioChat { Testo = tableMd, IsUtente = false });
                    }

                    if (pyOut != null)
                        messaggi.Add(new MessaggioChat { IsUtente = false, Result = pyOut.Result, Formula = pyOut.Formula, Explain = pyOut.Explain });

                    progressBar.Value = progressBar.Maximum;
                    loadingText.Text = "Completato";
                    UltimaDomandaUtente = domandaUtente;
                    UltimaQuerySql = sql;
                    UltimoPythonCode = py;
                    break; // success
                }
                catch (Exception ex) when (attempt == 0)
                {
                    correctionAttempted = true;
                    Logger.LogInfo($"Tentativo correzione per errore: {ex.Message}");
                    string corrected = await CorreggiRispostaAsync(domandaUtente, initialPrompt, gptReply, ex.ToString());
                    Logger.LogInfo($"Risposta corretta: {corrected}");
                    gptReply = corrected;
                }
                catch
                {
                    throw;
                }
            }


        }

        #endregion

        #region ▶︎ RAG – recupero similitudine
        private async Task<string?> ProvaRecuperoDaRAGAsync(string domanda)
        {
            float[] qVec = HnswIndexService.Normalize(
                               (await EmbeddingService.GetEmbeddingAsync(domanda)).ToArray());

            var top = vecIdx.Search(qVec, k: 3);
            if (top.Count == 0 || top[0].Score < 0.70f) return null;

            var row = await RAGService.GetByIdAsync(top[0].Id);
            if (row == null) return null;

            DataTable dt = await EseguiQueryAsync(row.QuerySql);
            string markdown = DataTableToMarkdown(dt);
            return $"**(Risposta da memoria RAG)**\n\n{markdown}\n\n_{row.Testo}_";
        }
       
        #endregion

        #region ▶︎ GPT helpers (CallGPT, spiegazione, prompt)
        private async Task<string> CallGPTAsync(string prompt)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OpenAIApiKey);
            var body = new
            {
                model = "gpt-4o",
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.2,
                top_p = 1.0
            };
            var res = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));
            dynamic js = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
            return js?.choices?[0]?.message?.content?.ToString() ?? string.Empty;
        }

        private async Task<string> OttieniSpiegazioneDaGPT(string domanda, string sql, DataTable dt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Domanda: " + domanda);
            sb.AppendLine("Query SQL: " + sql);
            sb.AppendLine("Risultati:\n" + DataTableToMarkdown(dt));
            sb.AppendLine("Spiega in breve.");
            return await CallGPTAsync(sb.ToString());
        }

        private static string BuildInitialPrompt(string domandaUtente)
        {

            var sb = new StringBuilder();
            /* ──────────────────── RULES ──────────────────── */
            sb.AppendLine("Sei un assistente per l’analisi di dati aziendali nel settore trasporti.");
            sb.AppendLine("Regole di risposta (obbligatorie):");
            sb.AppendLine("1.Se servono dati, genera UN SOLO blocco con una sintassi SQL Server (T-SQL) compreso tra delimitatori @ … @");
            sb.AppendLine("   • Inserisci subito dopo l'ultimo @ un flag: 1 se l’utente desidera anche una spiegazione, altrimenti 0.");
            sb.AppendLine("2. Se per rispondere è necessario calcolo matematico avanzato (regressioni, equazioni, correlazioni, forecast,");
            sb.AppendLine("   deviazione standard, ecc.) aggiungi subito dopo un blocco Python compreso tra ```python codicePython ```.");
            sb.AppendLine("   • Assumi che il DataFrame risultante dalla query sia già caricato in una variabile `df`.");
            sb.AppendLine("   • Usa SOLO le librerie: pandas, numpy, sympy, scipy, scikit-learn.");
            sb.AppendLine("   • Termina SEMPRE lo script con:");
            sb.AppendLine("       import json, sys");
            sb.AppendLine("       json.dump({'result': <tuo_valore>, 'formula': '<latex>', 'explain': '<breve testo>'}, sys.stdout)"); sb.AppendLine("3. Se la domanda si risolve con sole funzioni SQL standard (SUM, AVG, MAX, COUNT, MIN) NON generare il blocco Python.");
            sb.AppendLine("4. NON scrivere testo fuori dai blocchi. Nessuna spiegazione, commento o Markdown extra.");
            sb.AppendLine("5.Se la domanda utente richiede più passaggi sequenziali(es.verifica presenza dati, confronto tra periodi, fallback a previsioni, ecc.), devi rispondere con più blocchi numerati.");
            sb.AppendLine("• NON usare flag `@0` o `@1` in questo caso.");
            sb.AppendLine("• Ogni blocco SQL deve essere racchiuso tra `@...@3 - N`, dove N è il numero progressivo dello step(es: @...@3 - 1, @...@3 - 2, @...@3 - 3).");
            sb.AppendLine("• Se serve Python in uno step, il codice deve essere racchiuso in blocco ```python... ``` con il flag corrispondente `@3 - X` subito dopo il blocco.");
            sb.AppendLine("• Scrivi tutti i blocchi in ordine, senza testo fuori dai blocchi.");
            sb.AppendLine("• Ogni blocco sarà eseguito separatamente e i risultati saranno passati a un GPT analista per la sintesi finale.");
            sb.AppendLine();

            /* ──────────────────── FEW-SHOT EXAMPLE (helpful but not echoed) ──────────────────── */
            sb.AppendLine("Esempio (non mostrarlo all’utente, serve solo come riferimento di formato):");
            sb.AppendLine("@SELECT AVG([Consumo_km/l]) AS ConsumoMedio");
            sb.AppendLine("  FROM tbDatiConsumo");
            sb.AppendLine("  WHERE Targa = 'AB123CD' AND Data BETWEEN '2023-01-01' AND '2023-12-31'@0");
            sb.AppendLine();
            sb.AppendLine("@SELECT Data, [Consumo_km/l]");
            sb.AppendLine("  FROM tbDatiConsumo");
            sb.AppendLine("  WHERE Targa = 'AB123CD' AND Data >= '2020-01-01'@0");
            sb.AppendLine("```python");
            sb.AppendLine("import pandas as pd, numpy as np, json, sys");
            sb.AppendLine("from sklearn.linear_model import LinearRegression");
            sb.AppendLine("df['DataNum'] = pd.to_datetime(df['Data']).map(pd.Timestamp.toordinal)");
            sb.AppendLine("model = LinearRegression().fit(df[['DataNum']], df['Consumo_km/l'])");
            sb.AppendLine("future = pd.to_datetime(['2024-12-01']).map(pd.Timestamp.toordinal).values.reshape(-1,1)");
            sb.AppendLine("pred = model.predict(future)");
            sb.AppendLine("json.dump({'result': float(pred[0]), 'formula': 'y=mx+b', 'explain': 'retta di regressione'}, sys.stdout)"); sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Schema tabelle a cui dovrai fare riferimento per le query:");
            sb.AppendLine(SchemaDescrizione);
            sb.AppendLine();
            sb.AppendLine("Domanda utente: " + domandaUtente);
            return sb.ToString();
        }
        #endregion
        private async Task<string> GeneraQuerySqlAsync(string domanda)
        {
            string prompt = BuildInitialPrompt(domanda);
            return await CallGPTAsync(prompt);
        }

        private Task<string> CorreggiRispostaAsync(string domanda, string prompt, string risposta, string errore, CancellationToken ct = default) =>
        assistantClient.RunAsync(
        assistantId: ErrorCorrectorAssistantId,
        userMessage: $"DOMANDA:\n{domanda}\n\nPROMPT:\n{prompt}\n\nRISPOSTA:\n{risposta}\n\nERRORE:\n{errore}",
        ct: ct);

        private static (string Sql, int Flag, string? Python) EstraiSqlPythonEFlag(string gptReply)
        {
            var sqlMatch = Regex.Match(gptReply, "@(.+?)@(0|1)", RegexOptions.Singleline);
            if (!sqlMatch.Success) throw new Exception("GPT non ha restituito blocco @…@");

            string sql = sqlMatch.Groups[1].Value.Trim();
            int flag = int.Parse(sqlMatch.Groups[2].Value);

            string? py = null;
            var pyMatch = Regex.Match(gptReply, "```python(.*?)```", RegexOptions.Singleline);
            if (pyMatch.Success)
                py = pyMatch.Groups[1].Value.Trim();

#if DEBUG
            MessageBox.Show($"Estratta SQL ({sql.Length} char) – flag @{flag}\nPython length={(py?.Length ?? 0)}",
                            "DEBUG – EstraiSqlPythonEFlag");
#endif
            return (sql, flag, py);
        }




        #region ▶︎ SQL utils
        private async Task<DataTable> EseguiQueryAsync(string sql, CancellationToken ct = default)
        {
            try
            {
                using var conn = new SqlConnection(ConnString);
                using var cmd = new SqlCommand(sql, conn)
                {
                    CommandTimeout = 120
                };
                await conn.OpenAsync(ct);

                var dt = new DataTable();
                using var rdr = await cmd.ExecuteReaderAsync(ct);
                dt.Load(rdr);
                return dt;
            }
            catch (SqlException ex)
{
    var sb = new StringBuilder();
    sb.AppendLine("Errore SQL:");
    sb.AppendLine(ex.Message);

    var result = MessageBox.Show(
        sb.ToString() + "\n\nVuoi copiare la query negli appunti?",
        "Errore SQL",
        MessageBoxButton.YesNo,
        MessageBoxImage.Error);

    if (result == MessageBoxResult.Yes)
        Clipboard.SetText(sql);

#if DEBUG
    Debug.WriteLine("QUERY FALLITA:\n" + sql);
    Debug.WriteLine(ex);
#endif
    throw;
}

        }

        #endregion
        private string DataTableToMarkdown(DataTable table)
        {
            if (table == null || table.Rows.Count == 0)
                return "Nessun dato trovato.";

            var sb = new StringBuilder();

            // Header
            foreach (DataColumn col in table.Columns)
                sb.Append("| ").Append(col.ColumnName).Append(" ");
            sb.AppendLine("|");

            // Divider
            foreach (DataColumn col in table.Columns)
                sb.Append("|---");
            sb.AppendLine("|");

            // Rows
            foreach (DataRow row in table.Rows)
            {
                foreach (var cell in row.ItemArray)
                    sb.Append("| ").Append(cell.ToString()).Append(" ");
                sb.AppendLine("|");
            }

            return sb.ToString();
        }

        private static void SaveDataTableToCsv(DataTable table, string path,
                                               IProgress<(int current, int total)>? progress = null)
        {
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            // Header
            foreach (DataColumn col in table.Columns)
                csv.WriteField(col.ColumnName);
            csv.NextRecord();
            // Rows
            int total = table.Rows.Count;
            int count = 0;
            foreach (DataRow row in table.Rows)
            {
                foreach (var cell in row.ItemArray)
                    csv.WriteField(cell);
                csv.NextRecord();
                progress?.Report((++count, total));
            }
        }

        private static async Task<PythonOutput?> EseguiPythonAsync(string code, string csvPath)
        {
            const string pythonExe =
                @"C:\Users\omar.tagliabue\AppData\Local\Programs\Python\Python312\python.exe";

            string scriptFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.py");

            try
            {
                // 1️⃣ Scrivi il .py con import e codice utente
                var sb = new StringBuilder();
                sb.AppendLine("import pandas as pd");
                sb.AppendLine("import json, sys");
                sb.AppendLine($"df = pd.read_csv(r\"{csvPath}\")");
                sb.AppendLine(code);
                await File.WriteAllTextAsync(scriptFile, sb.ToString());

                // 2️⃣ Configura ed esegui il processo
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(scriptFile)
                };

                using var proc = Process.Start(psi)!;
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                // 3️⃣ Errore di esecuzione
                if (proc.ExitCode != 0)
                {
                    throw new Exception($"Python error: {stderr.Trim()}");
                }

                dynamic js = JsonConvert.DeserializeObject(stdout);
                string result = js.result?.ToString() ?? string.Empty;
                string formula = js.formula?.ToString() ?? string.Empty;
                string explain = js.explain?.ToString() ?? string.Empty;
                return new PythonOutput(result, formula, explain);
            }
            catch (Exception ex)
            {
                throw new Exception($"Python error: {ex.Message}");
            }
            finally
            {
                if (File.Exists(scriptFile))
                    File.Delete(scriptFile);
            }
        }




        private async void btnSalvaEmbedding_Click(object sender, RoutedEventArgs e)
        {
            if (messaggi.Count < 2) return;

            var domanda = messaggi[^2].Testo;
            var risposta = messaggi[^1].Testo;

            string input = $"{domanda}\nSQL:\n{risposta}";
            string testoEmbedding = await GeneraTestoEmbeddingAsync(input);

            string embeddingVector = await CalcolaEmbeddingVectorAsync(testoEmbedding); // usare modello embedding

            await SalvaInDatabaseEmbeddingAsync(domanda, risposta, testoEmbedding, embeddingVector, "admin");
            MessageBox.Show("Embedding salvato correttamente");
        }

        private Task<string> GeneraTestoEmbeddingAsync(string testo, CancellationToken ct = default) =>

            assistantClient.RunAsync(
     assistantId: "asst_JMGFXRnQZv4mz4cOim6lDnXe",
     userMessage: testo,
     ct: ct);



        private async Task<string> CalcolaEmbeddingVectorAsync(string testo)
        {
            var body = new
            {
                input = testo,
                model = "text-embedding-3-small" // o modello che preferisci
            };

            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OpenAIApiKey);
            var response = await httpClient.PostAsync("https://api.openai.com/v1/embeddings", content);
            string json = await response.Content.ReadAsStringAsync();
            dynamic parsed = JsonConvert.DeserializeObject(json);
            return JsonConvert.SerializeObject(parsed?.data?[0]?.embedding); // lo salvi come stringa JSON
        }


        private async Task SalvaInDatabaseEmbeddingAsync(string domanda, string sql, string testoEmbedding, string embedding, string utente)
        {
            using SqlConnection conn = new(ConnString);
            await conn.OpenAsync();
            string query = @"INSERT INTO tbEmbeddingRAG (DomandaUtente, QuerySQL, TestoEsplicativo, EmbeddingJson, UtenteValidazione, DataCreazione)
                     VALUES (@d, @q, @t, @v, @u, GETDATE())";
            using SqlCommand cmd = new(query, conn);
            cmd.Parameters.AddWithValue("@d", domanda);
            cmd.Parameters.AddWithValue("@q", sql);
            cmd.Parameters.AddWithValue("@t", testoEmbedding);
            cmd.Parameters.AddWithValue("@v", embedding);
            cmd.Parameters.AddWithValue("@u", utente);
            await cmd.ExecuteNonQueryAsync();
        }


        #region ▶︎ Parsing query
        private static bool TryEstrarreQuery(string testo, out string sql, out int flag)
        {
            var m = Regex.Match(testo, "@(.+?)@(0|1)", RegexOptions.Singleline);
            if (m.Success)
            {
                sql = m.Groups[1].Value.Trim();
                flag = int.Parse(m.Groups[2].Value);
                return true;
            }
            sql = string.Empty; flag = -1; return false;
        }
        #endregion
    }

    #region ▶︎ Model & converters
    public class MessaggioChat
    {
        public string? Testo { get; set; }
        public bool IsUtente { get; set; }
        public string? Result { get; set; }
        public string? Formula { get; set; }
        public string? Explain { get; set; }
        public bool HasPython => !string.IsNullOrWhiteSpace(Result);
    }

    public record PythonOutput(string Result, string Formula, string Explain);
    public class ChatAlignmentConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => (bool)v ? HorizontalAlignment.Left : HorizontalAlignment.Right; public object ConvertBack(object v, Type t, object p, CultureInfo c) => null!; }
    public class ChatBubbleBackgroundConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => new SolidColorBrush((bool)v ? Colors.LightGray : Colors.WhiteSmoke); public object ConvertBack(object v, Type t, object p, CultureInfo c) => null!; }
    public class ChatBubbleBorderBrushConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => new SolidColorBrush((bool)v ? Colors.Gray : Color.FromRgb(0, 122, 204)); public object ConvertBack(object v, Type t, object p, CultureInfo c) => null!; }
    public class UtenteToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Mostra il bottone solo se IsUtente è false
            if (value is bool isUtente && !isUtente)
                return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    /// <summary>
    ///  Restituisce Visible se la stringa non è vuota, altrimenti Collapsed.
    /// </summary>
    public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
                              object parameter, CultureInfo culture) =>
            string.IsNullOrWhiteSpace(value as string)
                ? Visibility.Collapsed
                : Visibility.Visible;

        public object ConvertBack(object value, Type targetType,
                                  object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
    #endregion

    #region ▶︎ Helper static classes (Embedding, RAG, VectorSearch)
    static class EmbeddingService
    {
        public static async Task<List<float>> GetEmbeddingAsync(string input)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MainWindow.OpenAIApiKey);
            var body = new { model = MainWindow.EmbModel, input };
            var res = await http.PostAsync("https://api.openai.com/v1/embeddings", new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));
            dynamic js = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());

#if DEBUG
            MessageBox.Show(
                $"Ricevuto vettore di {js.data[0].embedding.Count} dimensioni – modello “{MainWindow.EmbModel}”.",
                "DEBUG – Embedding");
#endif

            return js.data[0].embedding.ToObject<List<float>>();
        }
    }

    static class RAGService
    {
        /// <summary>
        /// Inserisce un nuovo record in tbEmbeddingRAG e restituisce l’ID appena creato.
        /// </summary>
        public static async Task<int> SalvaAsync(string domanda, string sql, string testo, string vettoreJson, string utente, string? pythonCode)
        {
            const string INS = @"
                                                                    INSERT INTO tbEmbeddingRAG
                                                                      (DomandaUtente, QuerySql, TestoEsplicativo, EmbeddingJson, UtenteValidazione, PythonCode)
                                                                    VALUES                                             
                                                                      (@domanda, @sql, @testo, @vector, @utente, @py);
                                                                    SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var c = new SqlConnection(MainWindow.ConnString);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(INS, c);

            cmd.Parameters.AddWithValue("@domanda", domanda);
            cmd.Parameters.AddWithValue("@sql", sql);
            cmd.Parameters.AddWithValue("@testo", testo);
            cmd.Parameters.AddWithValue("@vector", vettoreJson);
            cmd.Parameters.AddWithValue("@utente", utente);
            cmd.Parameters.AddWithValue("@py", (object?)pythonCode ?? DBNull.Value);

            object? idObj = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(idObj);
        }

        public record RagRow(string Domanda, string QuerySql, string Testo, string? PythonCode);
        public static async Task<RagRow?> GetByIdAsync(int id)
        {
            const string SEL = "SELECT DomandaUtente, QuerySQL, TestoEsplicativo, PythonCode FROM tbEmbeddingRAG WHERE ID=@id";
            await using var c = new SqlConnection(MainWindow.ConnString);
            await c.OpenAsync();
            var cmd = new SqlCommand(SEL, c);
            cmd.Parameters.AddWithValue("@id", id);
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (!rdr.Read()) return null;
            string? py = rdr.IsDBNull(3) ? null : rdr.GetString(3);
            return new RagRow(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2), py);
        }
    }





        #endregion
    }

