// MainWindow.xaml.cs – versione con RAG, feedback utente e salvataggio embedding
// 20 Giugno 2025

using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CamionReportGPT.Vector;     // namespace dove hai messo HnswIndexService
using HNSW.Net;
namespace CamionReportGPT
{
    public partial class MainWindow : Window
    {
        #region ✔︎ Costanti di configurazione
        public const string ConnString = "Server=192.168.1.24\\sgam;Database=PARATORI;User Id=sapara;Password=HAHAHHAHAHHA;Encrypt=True;TrustServerCertificate=True;";
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
        private readonly HnswIndexService vecIdx;
        private string? UltimaDomandaUtente;
        private string? UltimaQuerySql;
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
        }
        #endregion

        #region ▶︎ UI handlers (Invia / Enter / Feedback 👍)
        private async void btnInvia_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtInput.Text))
            {
                string domanda = txtInput.Text.Trim();
                txtInput.Clear();
                await GestioneConversazioneAsync(domanda);
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
                    testoEsplicativo, JsonConvert.SerializeObject(vec), UtenteCorrente);

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
                         : $"Top score={hits[0].Score:F3} (soglia 0.50) → {(hits[0].Score > 0.50f ? "OK" : "KO")}");
#endif


            if (hits.Count > 0 && hits[0].Score > 0.50f)          // soglia empirica
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
            string sqlPrompt = await GeneraQuerySqlAsync(domandaUtente);   // implementato altrove
            (string sql, int flag) = EstraiSqlEFlag(sqlPrompt);            // @0 / @1

            DataTable result = await EseguiQueryAsync(sql);
            string tableMd = DataTableToMarkdown(result);

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

            // 3) Memorizzo l’ultima domanda e query (servono in btnFeedback_Click)
            UltimaDomandaUtente = domandaUtente;
            UltimaQuerySql = sql;


    }

        #endregion

        #region ▶︎ RAG – recupero similitudine
        private async Task<string?> ProvaRecuperoDaRAGAsync(string domanda)
        {
            float[] qVec = HnswIndexService.Normalize(
                               (await EmbeddingService.GetEmbeddingAsync(domanda)).ToArray());

            var top = vecIdx.Search(qVec, k: 3);
            if (top.Count == 0 || top[0].Score < 0.50f) return null;

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
            sb.AppendLine("Sei un assistente dati. Se per rispondere devi consultare il DB, restituisci SOLO una query SQL tra due @, seguita da @0 o @1.");
            sb.AppendLine("Scegli @1 SOLO se la domanda richiede una spiegazione; altrimenti @0.");
            sb.AppendLine("Se scegli @1 spiega il risultato della query senza dire che proviene da una query");
            sb.AppendLine("Genera query T-SQL per SQL Server.");
            sb.AppendLine("►IMPORTANTE: genera SEMPRE e SOLO UNA query racchiusa tra @ e @, " +
                            "anche se servono più calcoli. Unisci sub-query con CTE, CROSS JOIN o UNION. " +
                            "Non produrre mai più di un blocco @…@.");
            sb.AppendLine("Quando crei più CTE, usa nelle CTE successive gli stessi alias esatti che hai definito in precedenza; evita di cambiare il nome");
            sb.AppendLine("Chiudi SEMPRE la query SQL direttamente con @1 o @0 senza newline dopo.");
            sb.AppendLine("Non includere ```sql o blocchi markdown nel codice. Scrivi solo testo SQL puro tra @ e @.");
            sb.AppendLine("Una sola query per risposta. No blocchi markdown.");
            sb.AppendLine();
            sb.AppendLine("Schema tabelle:");
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

        private static (string Sql, int Flag) EstraiSqlEFlag(string gptReply)
        {
            var m = Regex.Match(gptReply, "@(.+?)@(0|1)", RegexOptions.Singleline);
            if (!m.Success) throw new Exception("GPT non ha restituito blocco @…@");

            // ▼ NUOVE variabili locali
            string sql = m.Groups[1].Value.Trim();
            int flag = int.Parse(m.Groups[2].Value);

#if DEBUG
            MessageBox.Show($"Estratta SQL ({sql.Length} char) – flag @{flag}",
                            "DEBUG – EstraiSqlEFlag");
#endif
            return (sql, flag);
        }




        #region ▶︎ SQL utils
        private async Task<DataTable> EseguiQueryAsync(string sql)
        {
            var dt = new DataTable();
            await using var conn = new SqlConnection(ConnString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            dt.Load(rdr);
#if DEBUG
            DebugMsg("SQL Exec", $"Righe={dt.Rows.Count}, Colonne={dt.Columns.Count}");
#endif

            return dt;
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
    public class MessaggioChat { public string Testo { get; set; } = string.Empty; public bool IsUtente { get; set; } }
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
        public static async Task<int> SalvaAsync(string domanda, string sql, string testo, string vettoreJson, string utente)
        {
            const string INS = @"
                                                                    INSERT INTO tbEmbeddingRAG
                                                                      (DomandaUtente, QuerySql, TestoEsplicativo, EmbeddingJson, UtenteValidazione)
                                                                    VALUES
                                                                      (@domanda, @sql, @testo, @vector, @utente);
                                                                    SELECT CAST(SCOPE_IDENTITY() AS int);";

            await using var c = new SqlConnection(MainWindow.ConnString);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(INS, c);

            cmd.Parameters.AddWithValue("@domanda", domanda);
            cmd.Parameters.AddWithValue("@sql", sql);
            cmd.Parameters.AddWithValue("@testo", testo);
            cmd.Parameters.AddWithValue("@vector", vettoreJson);
            cmd.Parameters.AddWithValue("@utente", utente);

            object? idObj = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(idObj);
        }

        public record RagRow(string Domanda, string QuerySql, string Testo);
        public static async Task<RagRow?> GetByIdAsync(int id)
        {
            const string SEL = "SELECT DomandaUtente, QuerySQL, TestoEsplicativo FROM tbEmbeddingRAG WHERE ID=@id";
            await using var c = new SqlConnection(MainWindow.ConnString);
            await c.OpenAsync();
            var cmd = new SqlCommand(SEL, c);
            cmd.Parameters.AddWithValue("@id", id);
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (!rdr.Read()) return null;
            return new RagRow(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2));
        }
    }





        #endregion
    }

