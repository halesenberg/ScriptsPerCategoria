using UnityEngine;

namespace Bellavalle.Voice
{
    /// <summary>
    /// Rende il GameObject di WhisperManager persistente tra le scene,
    /// esattamente come GameManager. Gestisce il pattern singleton
    /// per evitare duplicati quando si carica una nuova scena.
    ///
    /// Setup:
    ///  1. Metti questo script sullo stesso GameObject che ha
    ///     WhisperManager + MicrophoneRecord + PushToTalkRecorder + VRPushToTalkInput
    ///  2. Il GameObject deve esistere nella PRIMA scena che carichi
    ///     (es. 00_Tutorial o 01_Stazione)
    ///  3. Se per sicurezza lo metti anche in altre scene, il duplicato
    ///     si autodistrugge — nessun conflitto
    /// </summary>
    public class PersistentWhisper : MonoBehaviour
    {
        public static PersistentWhisper Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
    }
}