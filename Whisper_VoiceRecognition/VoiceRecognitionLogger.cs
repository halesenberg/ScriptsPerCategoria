using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Bellavalle.Core;
using Bellavalle.Data;
using Whisper;

namespace Bellavalle.Voice
{
    /// <summary>
    /// Logging per lo studio sugli accenti: per ogni tentativo di riconoscimento
    /// vocale scrive una riga in un CSV con il testo trascritto, se ha trovato
    /// un match tra le frasi attese del nodo di dialogo, e la confidenza media
    /// che Whisper assegna ai token riconosciuti.
    ///
    /// NON salva l'audio (solo testo, come deciso) e NON conosce l'accento del
    /// parlante: quello va incrociato DOPO, in fase di analisi, tra
    /// ParticipantId e il questionario demografico compilato durante il test.
    ///
    /// Setup:
    ///  1. Metti questo script sullo stesso GameObject di PushToTalkRecorder
    ///     (es. WhisperManager) — convive senza conflitti con VRPushToTalkInput
    ///     e KeyboardPushToTalkInput, è solo un ascoltatore, non tocca nessuno
    ///     dei due.
    ///  2. Assegna pushToTalkRecorder nell'Inspector.
    ///  3. IMPORTANTE — sul WhisperManager, spunta "Enable Tokens". Senza
    ///     quello Whisper non calcola la confidenza per token: il testo viene
    ///     comunque loggato, ma la colonna AvgConfidence resta vuota.
    ///  4. Prima di ogni sessione di test con un partecipante, imposta
    ///     ParticipantId nell'Inspector (es. "P01") — è l'unico modo in cui
    ///     questo log si collega al questionario demografico compilato a
    ///     parte. Se lo lasci vuoto, la sessione viene comunque loggata ma
    ///     con la colonna ParticipantId vuota — non riuscirai a incrociarla.
    ///  5. Il file esce in Application.persistentDataPath (il percorso esatto
    ///     compare in Console all'avvio) — si accumula, riga dopo riga, tra
    ///     una sessione e l'altra: non lo sovrascrive mai, solo append.
    ///  6. Serve anche PushToTalkRecorder.cs aggiornato (quello che ti ho
    ///     mandato insieme a questo file): aggiunge l'evento
    ///     OnTranscriptionResultReady da cui questo script legge la confidenza.
    ///     Senza quell'aggiornamento questo componente non compila.
    /// </summary>
    public class VoiceRecognitionLogger : MonoBehaviour
    {
        [Header("Riferimenti")]
        [SerializeField] PushToTalkRecorder pushToTalkRecorder;

        [Header("Sessione di test")]
        [Tooltip("Da incrociare col questionario demografico compilato durante il test. Impostalo prima di ogni sessione.")]
        [SerializeField] string participantId = "";

        [Header("File")]
        [SerializeField] string fileName = "voice_recognition_log.csv";

        [Header("Debug")]
        [SerializeField] bool logToConsole = true;

        string _filePath;
        DialogueNode _currentNode;
        string _currentTreeId = "";

        // ── Unity lifecycle ────────────────────────────────────────────
        void OnEnable()
        {
            if (pushToTalkRecorder != null)
                pushToTalkRecorder.OnTranscriptionResultReady += OnTranscriptionResultReady;

            EventBus.On(GameEvent.NodeEntered, OnNodeEntered);
            EventBus.On(GameEvent.DialogueStarted, OnDialogueStarted);
            EventBus.On(GameEvent.DialogueEnded, OnDialogueEnded);
        }

        void OnDisable()
        {
            if (pushToTalkRecorder != null)
                pushToTalkRecorder.OnTranscriptionResultReady -= OnTranscriptionResultReady;

            EventBus.Off(GameEvent.NodeEntered, OnNodeEntered);
            EventBus.Off(GameEvent.DialogueStarted, OnDialogueStarted);
            EventBus.Off(GameEvent.DialogueEnded, OnDialogueEnded);
        }

        void Start()
        {
            _filePath = Path.Combine(Application.persistentDataPath, fileName);

            if (string.IsNullOrWhiteSpace(participantId))
                Debug.LogWarning("[VoiceRecognitionLogger] ParticipantId vuoto — impostalo nell'Inspector prima " +
                                  "di iniziare il test, altrimenti le righe di questa sessione non si potranno " +
                                  "incrociare col questionario.");

            EnsureHeader();

            if (logToConsole)
                Debug.Log($"[VoiceRecognitionLogger] Log su: {_filePath}");
        }

        /// <summary>Puoi anche impostarlo da codice/UI invece che dall'Inspector, se preferisci.</summary>
        public void SetParticipantId(string id) => participantId = id;

        // ── Tracciamento contesto: quale dialogo/nodo era attivo ────────
        void OnDialogueStarted(object data) => _currentTreeId = data as string ?? "";

        void OnDialogueEnded(object data)
        {
            _currentTreeId = "";
            _currentNode = null;
        }

        void OnNodeEntered(object data) => _currentNode = data as DialogueNode;

        // ── Riceve testo + dati di confidenza da Whisper ────────────────
        void OnTranscriptionResultReady(WhisperResult result)
        {
            if (result == null) return;

            string text = (result.Result ?? "").Trim();
            float avgConfidence = ComputeAverageConfidence(result);
            int tokenCount = CountRealTokens(result);

            string expectedPhrases = "";
            string matchedOption = "";
            string isMatch = "n/d";

            var node = _currentNode;
            if (node != null)
            {
                if (node.isNameCapture)
                {
                    expectedPhrases = "[nome libero]";
                }
                else if (node.options != null && node.options.Length > 0)
                {
                    expectedPhrases = string.Join(" | ", node.options.Select(o => o.text_IT));

                    int matchIndex = VoiceMatchUtils.FindMatchingOption(text, node.options);
                    isMatch = matchIndex >= 0 ? "1" : "0";
                    matchedOption = matchIndex >= 0 ? node.options[matchIndex].text_IT : "";
                }
            }

            WriteRow(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                participantId,
                SceneManager.GetActiveScene().name,
                _currentTreeId,
                node?.nodeId ?? "",
                node?.npcLine_IT ?? "",
                expectedPhrases,
                text,
                matchedOption,
                isMatch,
                avgConfidence >= 0f ? avgConfidence.ToString("F3") : "",
                tokenCount.ToString()
            );

            if (logToConsole)
            {
                string confStr = avgConfidence >= 0f ? avgConfidence.ToString("F2") : "n/d";
                Debug.Log($"[VoiceRecognitionLogger] \"{text}\" — match={isMatch} confidenza media={confStr}");
            }
        }

        // ── Confidenza media (esclude i token speciali tipo [EOT]/[BEG]) ─
        float ComputeAverageConfidence(WhisperResult result)
        {
            if (result.Segments == null) return -1f;

            float sum = 0f;
            int count = 0;

            foreach (var seg in result.Segments)
            {
                if (seg.Tokens == null) continue; // EnableTokens spento sul WhisperManager
                foreach (var tok in seg.Tokens)
                {
                    if (tok.IsSpecial) continue;
                    sum += tok.Prob;
                    count++;
                }
            }

            return count > 0 ? sum / count : -1f;
        }

        int CountRealTokens(WhisperResult result)
        {
            if (result.Segments == null) return 0;

            int count = 0;
            foreach (var seg in result.Segments)
            {
                if (seg.Tokens == null) continue;
                foreach (var tok in seg.Tokens)
                    if (!tok.IsSpecial) count++;
            }
            return count;
        }

        // ── Scrittura CSV ────────────────────────────────────────────────
        void EnsureHeader()
        {
            if (File.Exists(_filePath)) return;

            string header = string.Join(",",
                "Timestamp", "ParticipantId", "Scene", "TreeId", "NodeId",
                "NpcLineIT", "ExpectedPhrases", "TranscribedText",
                "MatchedOption", "IsMatch", "AvgConfidence", "TokenCount");

            File.WriteAllText(_filePath, header + Environment.NewLine, Encoding.UTF8);
        }

        void WriteRow(params string[] fields)
        {
            try
            {
                string line = string.Join(",", fields.Select(CsvEscape));
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VoiceRecognitionLogger] Scrittura fallita: {e}");
            }
        }

        string CsvEscape(string field)
        {
            field ??= "";
            bool needsQuotes = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
            if (needsQuotes)
                field = "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
