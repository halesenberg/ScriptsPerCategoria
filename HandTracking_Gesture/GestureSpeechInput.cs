using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.SceneManagement;
using Bellavalle.UI;
using Bellavalle.Voice;

namespace Bellavalle.Voice
{
    public class GestureSpeechInput : MonoBehaviour
    {
        public enum GestureMode { PuntaSu, CheVuoi }

        [Header("Riferimenti fissi (persistenti)")]
        [SerializeField] PushToTalkRecorder pushToTalk;

        // Presi automaticamente a ogni scena — niente più da trascinare in Inspector,
        // perché questo componente è persistente ma moveProvider/inventoryUI cambiano scena per scena.
        FistDynamicMoveProvider moveProvider;
        InventoryUI inventoryUI;

        [Header("Quale gesto usare")]
        [SerializeField] GestureMode gestureMode = GestureMode.PuntaSu;

        [Header("Rilevamento — PuntaSu")]
        [Range(0f, 1f)]
        [SerializeField] float pointUpThreshold = 0.7f;
        [SerializeField] float curledDistance = 0.06f;

        [Header("Rilevamento — CheVuoi")]
        [SerializeField] float pinchDistance = 0.04f;
        [Range(0f, 1f)]
        [SerializeField] float cheVuoiUpThreshold = 0.6f;

        [Header("Rilevamento 'palmi in giù'")]
        [Range(0f, 1f)]
        [SerializeField] float palmDownThreshold = 0.7f;

        [Header("Debug")]
        [SerializeField] bool logToConsole = true;

        XRHandSubsystem _handSubsystem;
        bool _listening;

        void Awake()
        {
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            if (subsystems.Count > 0)
                _handSubsystem = subsystems[0];
            else
                Debug.LogWarning("[GestureSpeechInput] XRHandSubsystem non trovato!");
        }

        void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            RefreshSceneReferences(); // copre anche la scena già attiva all'avvio
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            RefreshSceneReferences();
        }

        void RefreshSceneReferences()
        {
            moveProvider = FindFirstObjectByType<FistDynamicMoveProvider>();
            inventoryUI = FindFirstObjectByType<InventoryUI>();

            if (logToConsole)
                Debug.Log($"[GestureSpeechInput] Riferimenti scena aggiornati — moveProvider: {(moveProvider != null ? "trovato" : "assente")}, inventoryUI: {(inventoryUI != null ? "trovato" : "assente")}");
        }

        void Update()
        {
            if (_handSubsystem == null) return;

            if (!_listening)
            {
                if (IsTargetGesture(_handSubsystem.leftHand) || IsTargetGesture(_handSubsystem.rightHand))
                    StartListening();
            }
            else
            {
                if (IsPalmDown(_handSubsystem.leftHand) && IsPalmDown(_handSubsystem.rightHand))
                    StopListening();
            }
        }

        bool IsTargetGesture(XRHand hand) =>
            gestureMode == GestureMode.PuntaSu ? IsPointingUp(hand) : IsCheVuoi(hand);

        void StartListening()
        {
            if (pushToTalk == null)
            {
                Debug.LogWarning("[GestureSpeechInput] Push To Talk non assegnato — il gesto non fa nulla.");
                return;
            }

            _listening = true;
            if (moveProvider != null) moveProvider.enabled = false;
            pushToTalk.StartRecording();

            if (logToConsole) Debug.Log($"[GestureSpeechInput] Gesto '{gestureMode}' rilevato → microfono ATTIVO, movimento disattivato.");
        }

        void StopListening()
        {
            _listening = false;
            pushToTalk.StopRecording();

            bool inventoryStillOpen = inventoryUI != null && inventoryUI.IsOpen;
            if (moveProvider != null && !inventoryStillOpen) moveProvider.enabled = true;

            if (logToConsole) Debug.Log("[GestureSpeechInput] Palmi tornati in giù → microfono FERMATO" +
                (inventoryStillOpen ? " (movimento resta bloccato: zaino ancora aperto)." : ", movimento riattivato."));
        }

        bool IsPointingUp(XRHand hand)
        {
            if (!hand.isTracked) return false;
            try
            {
                var indexTip = hand.GetJoint(XRHandJointID.IndexTip);
                var indexProximal = hand.GetJoint(XRHandJointID.IndexProximal);
                if (!indexTip.TryGetPose(out Pose tipPose)) return false;
                if (!indexProximal.TryGetPose(out Pose proxPose)) return false;

                Vector3 indexDir = (tipPose.position - proxPose.position).normalized;
                if (Vector3.Dot(indexDir, Vector3.up) <= pointUpThreshold) return false;

                var palmJoint = hand.GetJoint(XRHandJointID.Palm);
                if (!palmJoint.TryGetPose(out Pose palmPose)) return false;

                return IsFingerCurled(hand, XRHandJointID.MiddleTip, palmPose.position)
                    && IsFingerCurled(hand, XRHandJointID.RingTip, palmPose.position)
                    && IsFingerCurled(hand, XRHandJointID.LittleTip, palmPose.position);
            }
            catch { return false; }
        }

        bool IsFingerCurled(XRHand hand, XRHandJointID tipId, Vector3 palmPosition)
        {
            var tipJoint = hand.GetJoint(tipId);
            if (!tipJoint.TryGetPose(out Pose tipPose)) return false;
            return Vector3.Distance(tipPose.position, palmPosition) < curledDistance;
        }

        bool IsCheVuoi(XRHand hand)
        {
            if (!hand.isTracked) return false;
            try
            {
                var thumbTip = hand.GetJoint(XRHandJointID.ThumbTip);
                var indexTip = hand.GetJoint(XRHandJointID.IndexTip);
                var middleTip = hand.GetJoint(XRHandJointID.MiddleTip);
                var ringTip = hand.GetJoint(XRHandJointID.RingTip);
                var littleTip = hand.GetJoint(XRHandJointID.LittleTip);
                var wristJoint = hand.GetJoint(XRHandJointID.Wrist);

                if (!thumbTip.TryGetPose(out Pose thumbPose)) return false;
                if (!indexTip.TryGetPose(out Pose indexPose)) return false;
                if (!middleTip.TryGetPose(out Pose middlePose)) return false;
                if (!ringTip.TryGetPose(out Pose ringPose)) return false;
                if (!littleTip.TryGetPose(out Pose littlePose)) return false;
                if (!wristJoint.TryGetPose(out Pose wristPose)) return false;

                Vector3 centroid = (thumbPose.position + indexPose.position + middlePose.position
                                   + ringPose.position + littlePose.position) / 5f;

                bool allPinched =
                    Vector3.Distance(thumbPose.position, centroid) < pinchDistance &&
                    Vector3.Distance(indexPose.position, centroid) < pinchDistance &&
                    Vector3.Distance(middlePose.position, centroid) < pinchDistance &&
                    Vector3.Distance(ringPose.position, centroid) < pinchDistance &&
                    Vector3.Distance(littlePose.position, centroid) < pinchDistance;

                if (!allPinched) return false;

                Vector3 handDir = (centroid - wristPose.position).normalized;
                return Vector3.Dot(handDir, Vector3.up) > cheVuoiUpThreshold;
            }
            catch { return false; }
        }

        bool IsPalmDown(XRHand hand)
        {
            if (!hand.isTracked) return false;
            try
            {
                var palmJoint = hand.GetJoint(XRHandJointID.Palm);
                if (!palmJoint.TryGetPose(out Pose palmPose)) return false;

                float dot = Vector3.Dot(palmPose.rotation * Vector3.down, Vector3.down);
                return dot > palmDownThreshold;
            }
            catch { return false; }
        }
    }
}