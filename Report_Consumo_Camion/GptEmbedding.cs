using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CamionReportGPT
{
    public sealed class OpenAIAssistantClient
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private const string BaseUrl = "https://api.openai.com/v1/";

        public OpenAIAssistantClient(string apiKey)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _http = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(90)
            };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey); 
            _http.DefaultRequestHeaders.Add("OpenAI-Beta", "assistants=v2");
        }

        /// <summary>
        /// Invia <paramref name="userMessage"/> all’assistant indicato e restituisce l’output finale.
        /// </summary>
        public async Task<string> RunAsync(string assistantId, string userMessage,
                                           int pollDelayMs = 1200,
                                           CancellationToken ct = default)
        {
            // 1) crea thread
            string threadId = await CreateThreadAsync(ct);

            // 2) aggiunge msg dell’utente
            await CreateMessageAsync(threadId, userMessage, ct);

            // 3) avvia run
            string runId = await CreateRunAsync(threadId, assistantId, ct);

            // 4) poll finché status == completed / failed / cancelled
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                dynamic runStatus = await GetAsync($"threads/{threadId}/runs/{runId}", ct);
                string status = runStatus.status;
                if (status == "completed")
                    break;

                if (status is "failed" or "cancelled" or "expired")
                    throw new Exception($"Run {runId} ended with status '{status}'.\n{runStatus}");

                await Task.Delay(pollDelayMs, ct);
            }

            // 5) recupera l’ultimo messaggio (assistant)
            dynamic messages = await GetAsync(
                $"threads/{threadId}/messages?limit=1&order=desc", ct);

            return messages.data[0].content[0].text.value.ToString().Trim();
        }

        /* ---------- helper privati ---------- */

        private async Task<string> CreateThreadAsync(CancellationToken ct)
        {
            dynamic res = await PostAsync("threads", new { }, ct);
            return res.id;
        }

        private Task CreateMessageAsync(string threadId, string content, CancellationToken ct) =>
            PostAsync($"threads/{threadId}/messages",
                      new { role = "user", content }, ct);

        private async Task<string> CreateRunAsync(string threadId, string assistantId,
                                                  CancellationToken ct)
        {
            dynamic res = await PostAsync($"threads/{threadId}/runs",
                                          new { assistant_id = assistantId }, ct);
            return res.id;
        }

        private async Task<dynamic> PostAsync(string url, object body, CancellationToken ct)
        {
            var resp = await _http.PostAsync(
                url,
                new StringContent(JsonConvert.SerializeObject(body),
                                  Encoding.UTF8, "application/json"), ct);

            string json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{resp.StatusCode}: {json}");

            return JsonConvert.DeserializeObject(json);
        }

        private async Task<dynamic> GetAsync(string url, CancellationToken ct)
        {
            var resp = await _http.GetAsync(url, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{resp.StatusCode}: {json}");

            return JsonConvert.DeserializeObject(json);
        }
    }
}
