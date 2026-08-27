using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using Bellavalle.Data;
using Bellavalle.Voice;
using Whisper;

namespace Bellavalle.AI
{
    /// <summary>
    /// "Guida grammaticale" — unico uso di Ollama nel gioco (niente chat NPC
    /// libera, niente "non ho capito" potenziato: quelle idee sono state
    /// scartate). Il player è un principiante ASSOLUTO: chiede in inglese,
    /// nominando il verbo in inglese ("how do you conjugate 'to have'?"),
    /// perché non conosce ancora la parola italiana. Riceve una risposta
    /// bilingue con la coniugazione corretta.
    ///
    /// Architettura in DUE PASSI, per evitare allucinazioni:
    ///  1. Ollama legge la domanda inglese e fa il mapping inglese→italiano
    ///     ("to have" → "avere"), restituendo SOLO un JSON
    ///     {"verbo": "...", "tempo": "..."} — non genera mai la
    ///     coniugazione, capisce solo l'intento (quale verbo, quale tempo).
    ///  2. La coniugazione vera arriva SEMPRE da VerbConjugationDatabase,
    ///     scritta e verificata a mano da te. Ollama non tocca mai il
    ///     testo che il player legge/sente come risposta.
    ///
    /// Se il verbo non è nel database, o il tempo richiesto non è ancora
    /// supportato (v1 = solo presente), il player riceve un messaggio
    /// fisso di aiuto in inglese — anche questo mai generato da Ollama.
    ///
    /// Setup:
    ///  1. Metti questo script sullo stesso GameObject persistente del
    ///     WhisperManager (quello con PersistentWhisper.cs), o su un
    ///     GameObject vicino ad esso — es. "AIGrammarHelper".
    ///  2. Assegna verbDatabase = il tuo asset VerbConjugationDatabase.
    ///  3. Assegna whisperManager = il componente WhisperManager sul
    ///     GameObject persistente.
    ///  4. Assegna pushToTalkRecorder = il componente PushToTalkRecorder sullo
    ///     stesso GameObject persistente. Fatto questo, ogni trascrizione
    ///     arriva già automaticamente qui (OnEnable si collega da solo) —
    ///     non serve scrivere nessuna riga di codice per il collegamento.
    ///  5. Prima di avviare la registrazione nella tab AI, chiama
    ///     EnterEnglishMode(); quando il player chiude la tab, chiama
    ///     ExitEnglishMode() per ripristinare l'italiano.
    ///  6. Ascolta OnAnswerReady (risposta valida), OnHelpNeeded (domanda
    ///     capita ma fuori dallo scope attuale) e OnError (problema
    ///     tecnico, es. Ollama non in esecuzione) per mostrare il testo
    ///     nel pannello.
    ///
    /// Uso tipico:
    ///   aiGrammarHelper.EnterEnglishMode();
    ///   // ... player parla in inglese, Whisper trascrive ...
    ///   aiGrammarHelper.AskAboutVerb("how do you conjugate 'to have' in the present tense?");
    /// </summary>
    public class AIGrammarHelperService : MonoBehaviour
    {
        [Header("Ollama")]
        [SerializeField] string ollamaUrl = "http://localhost:11434/api/generate";
        [SerializeField] string model = "mistral";

        [Header("Dati")]
        [SerializeField] VerbConjugationDatabase verbDatabase;

        [Header("Whisper (per il toggle inglese/italiano)")]
        [SerializeField] WhisperManager whisperManager;

        [Header("Input")]
        [Tooltip("Assegnalo per collegare tutto SOLO dall'Inspector, senza scrivere " +
                 "nessuna riga di codice: appena assegnato, ogni trascrizione di " +
                 "PushToTalkRecorder viene automaticamente mandata a AskAboutVerb. " +
                 "È l'unico uso di Ollama nel gioco, quindi non serve nessuno smistamento.")]
        [SerializeField] PushToTalkRecorder pushToTalkRecorder;

        [Header("Debug")]
        [SerializeField] bool logToConsole = true;

        /// <summary>Risposta valida pronta da mostrare (coniugazione vera, da database).</summary>
        public event Action<string> OnAnswerReady;

        /// <summary>Domanda capita ma fuori dallo scope attuale (verbo non supportato,
        /// tempo non ancora implementato, o intento non riconosciuto). Testo fisso, non da Ollama.</summary>
        public event Action<string> OnHelpNeeded;

        /// <summary>Problema tecnico (Ollama non raggiungibile, risposta vuota/non interpretabile).</summary>
        public event Action<string> OnError;

        bool _isWaitingResponse;
        string _languageBeforeEnglishMode;

        public bool IsWaitingResponse => _isWaitingResponse;

        // ── Auto-collegamento a PushToTalkRecorder (zero codice da scrivere) ──
        void OnEnable()
        {
            if (pushToTalkRecorder != null)
                pushToTalkRecorder.OnTranscriptionReady += AskAboutVerb;
        }

        void Awake()
        {
            string configPath = System.IO.Path.Combine(Application.persistentDataPath, "ollama_config.txt");
            if (System.IO.File.Exists(configPath))
            {
                string overrideUrl = System.IO.File.ReadAllText(configPath).Trim();
                if (!string.IsNullOrEmpty(overrideUrl))
                {
                    ollamaUrl = overrideUrl;
                    if (logToConsole) Debug.Log($"[AIGrammarHelperService] ollamaUrl da file di config: {ollamaUrl}");
                }
            }
            else if (logToConsole)
            {
                Debug.Log($"[AIGrammarHelperService] Nessun ollama_config.txt trovato, uso il default: {ollamaUrl}");
            }
        }

        void OnDisable()
        {
            if (pushToTalkRecorder != null)
                pushToTalkRecorder.OnTranscriptionReady -= AskAboutVerb;
        }

        // ── Toggle lingua Whisper ────────────────────────────────────────
        /// <summary>Chiamalo prima di avviare la registrazione per questa modalità:
        /// il player deve poter parlare in inglese.</summary>
        public void EnterEnglishMode()
        {
            if (whisperManager == null) return;
            _languageBeforeEnglishMode = whisperManager.language;
            whisperManager.language = "en";
            if (logToConsole) Debug.Log("[AIGrammarHelperService] Whisper in modalità inglese.");
        }

        /// <summary>Chiamalo quando il player esce dalla modalità guida grammaticale,
        /// per tornare alla trascrizione italiana normale.</summary>
        public void ExitEnglishMode()
        {
            if (whisperManager == null) return;
            whisperManager.language = string.IsNullOrEmpty(_languageBeforeEnglishMode)
                ? "it"
                : _languageBeforeEnglishMode;
            if (logToConsole) Debug.Log("[AIGrammarHelperService] Whisper tornato a: " + whisperManager.language);
        }

        // ── API pubblica — collega qui PushToTalkRecorder.OnTranscriptionReady ──
        public void AskAboutVerb(string playerQuestionEN)
        {
            if (string.IsNullOrWhiteSpace(playerQuestionEN))
            {
                if (logToConsole) Debug.Log("[AIGrammarHelperService] Domanda vuota, ignorata.");
                return;
            }

            if (verbDatabase == null)
            {
                Debug.LogError("[AIGrammarHelperService] VerbConjugationDatabase non assegnato!");
                OnError?.Invoke("Database verbi non configurato.");
                return;
            }

            if (_isWaitingResponse)
            {
                if (logToConsole) Debug.Log("[AIGrammarHelperService] Richiesta già in corso, ignorata.");
                return;
            }

            StartCoroutine(SendExtractionRequest(playerQuestionEN));
        }

        // ── Costruzione del prompt di SOLA estrazione (mai generazione) ──
        // Il player è un principiante ASSOLUTO: non conosce ancora l'italiano,
        // quindi nomina i verbi in inglese ("have", "to have"), mai in italiano.
        // Ollama deve fare un mapping inglese -> infinito italiano, non un
        // semplice riconoscimento di parole italiane nella frase.
        string BuildExtractionPrompt(string playerQuestionEN)
        {
            string mappingBlock = verbDatabase.BuildEnglishToItalianMappingBlock();

            return
                "You are a strict information-extraction tool inside an Italian-learning VR game.\n" +
                "The player is an English speaker with NO knowledge of Italian yet. They will ask,\n" +
                "entirely in English, how to conjugate a verb — naming it in ENGLISH (e.g. \"have\",\n" +
                "\"to have\", \"having\"), never in Italian, because they don't know the Italian word.\n\n" +
                "Here are the only verbs you can help with, given as English -> Italian pairs:\n" +
                $"{mappingBlock}\n" +
                "Your ONLY job is to figure out which of these verbs (if any) the player means, and\n" +
                "answer with EXACTLY one line of JSON, nothing else — no explanation, no markdown:\n" +
                "{\"verbo\": \"<Italian infinitive from the list above, or null>\", \"tempo\": \"<value or null>\"}\n\n" +
                "Rules:\n" +
                "1. \"verbo\" MUST be the ITALIAN infinitive from the list above (e.g. \"avere\"), NEVER " +
                "the English word. Match by meaning, even if the player says \"have\", \"to have\", " +
                "\"having\", or phrases it indirectly. If it doesn't clearly match one of the listed " +
                "verbs, set \"verbo\" to null.\n" +
                "2. \"tempo\" must be \"presente\" if the player is asking about the present tense, or " +
                "does not specify a tense (assume presente by default). If they clearly ask about a " +
                "different tense (past, future, imperfect, etc.), set \"tempo\" to that tense in English " +
                "(e.g. \"past\"). Do NOT translate it, do NOT invent conjugated forms.\n" +
                "3. NEVER output the conjugated verb forms yourself. NEVER add explanation or commentary. " +
                "Output ONLY the JSON object, on a single line.\n\n" +
                $"Question: \"{playerQuestionEN}\"";
        }

        // ── Chiamata HTTP a Ollama (solo per l'estrazione dell'intento) ──
        IEnumerator SendExtractionRequest(string playerQuestionEN)
        {
            _isWaitingResponse = true;

            string prompt = BuildExtractionPrompt(playerQuestionEN);
            string jsonBody = BuildJsonRequest(model, prompt);

            if (logToConsole) Debug.Log($"[AIGrammarHelperService] Invio a Ollama: \"{playerQuestionEN}\"");

            using (var request = new UnityWebRequest(ollamaUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                _isWaitingResponse = false;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string err = $"[AIGrammarHelperService] Errore Ollama: {request.error}. " +
                                 "Verifica che Ollama sia in esecuzione (ollama run mistral).";
                    Debug.LogError(err);
                    OnError?.Invoke(err);
                    yield break;
                }

                string rawGenerated = ExtractOllamaResponseField(request.downloadHandler.text);
                if (string.IsNullOrWhiteSpace(rawGenerated))
                {
                    Debug.LogWarning("[AIGrammarHelperService] Risposta vuota da Ollama.");
                    OnError?.Invoke("Risposta vuota da Ollama.");
                    yield break;
                }

                if (logToConsole) Debug.Log($"[AIGrammarHelperService] Estrazione grezza: \"{rawGenerated}\"");

                ResolveAnswer(rawGenerated);
            }
        }

        // ── Da testo estratto a risposta finale (SEMPRE deterministica) ──
        void ResolveAnswer(string rawGenerated)
        {
            string verbo = ExtractJsonField(rawGenerated, "verbo");
            string tempo = ExtractJsonField(rawGenerated, "tempo");

            if (string.IsNullOrEmpty(verbo))
            {
                // Messaggio ed esempio SEMPRE in inglese: il player non conosce
                // ancora l'italiano, quindi l'esempio deve nominare il verbo in
                // inglese, come farebbe lui davvero.
                OnHelpNeeded?.Invoke(
                    "I didn't understand which verb you're asking about. Try for example: " +
                    "\"How do you conjugate 'to have' in the present tense?\"");
                return;
            }

            var entry = verbDatabase.Get(verbo);
            if (entry == null)
            {
                // Messaggio al player: SEMPRE in inglese/glosse inglesi — non conosce
                // ancora l'italiano, quindi elencare gli infiniti italiani non aiuta.
                string supportedList = string.Join(", ", verbDatabase.SupportedEnglishGlosses());
                OnHelpNeeded?.Invoke(
                    $"For now I can only help you with these verbs: {supportedList}.");
                return;
            }

            if (string.IsNullOrEmpty(tempo) || !tempo.Equals("presente", StringComparison.OrdinalIgnoreCase))
            {
                OnHelpNeeded?.Invoke(
                    $"For now I can only explain \"{entry.infinitiveEN}\" in the present tense.");
                return;
            }

            OnAnswerReady?.Invoke(entry.BuildPresenteText());
        }

        // ── Estrazione di un campo dal JSON grezzo generato da Ollama ──
        // Tollerante: il modello a volte aggiunge testo/markdown intorno al JSON,
        // o omette le virgolette su "null" — questo regex gestisce entrambi i casi.
        static string ExtractJsonField(string rawText, string fieldName)
        {
            if (string.IsNullOrEmpty(rawText)) return null;

            var pattern = $"\"{fieldName}\"\\s*:\\s*\"?([a-zA-Z]+)\"?";
            var match = Regex.Match(rawText, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return null;

            string value = match.Groups[1].Value;
            return value.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : value.ToLowerInvariant();
        }

        // ── Costruzione JSON richiesta verso Ollama (manuale, niente dipendenze extra) ──
        string BuildJsonRequest(string modelName, string prompt)
        {
            string escapedPrompt = EscapeJson(prompt);
            string escapedModel = EscapeJson(modelName);
            return "{\"model\":\"" + escapedModel + "\"," +
                   "\"prompt\":\"" + escapedPrompt + "\"," +
                   "\"stream\":false}";
        }

        string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\")
                     .Replace("\"", "\\\"")
                     .Replace("\n", "\\n")
                     .Replace("\r", "");
        }

        // ── Estrazione del campo "response" dalla busta JSON di Ollama ──
        // (stessa tecnica usata in AIDialogueService.cs)
        string ExtractOllamaResponseField(string json)
        {
            const string key = "\"response\":\"";
            int start = json.IndexOf(key);
            if (start < 0) return null;
            start += key.Length;

            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    if (next == 'n') { sb.Append(' '); i++; continue; }
                    if (next == '"') { sb.Append('"'); i++; continue; }
                    if (next == '\\') { sb.Append('\\'); i++; continue; }
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
