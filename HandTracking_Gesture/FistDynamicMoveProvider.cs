using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

public class FistDynamicMoveProvider : DynamicMoveProvider
{
    [Range(0f, 1f)]
    public float fistThreshold = 0.7f;

    private XRHandSubsystem _handSubsystem;

    protected override void Awake()
    {
        base.Awake();

        var subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        if (subsystems.Count > 0)
            _handSubsystem = subsystems[0];
        else
            Debug.LogWarning("XRHandSubsystem non trovato!");
    }

    protected override Vector3 ComputeDesiredMove(Vector2 input)
    {
        
        bool leftFist = IsPalmUp(_handSubsystem.leftHand);
        bool rightFist = IsPalmUp(_handSubsystem.rightHand);

        // Sostituisce l'input con il gesto
        Vector2 fistInput = Vector2.zero;
        if (leftFist || rightFist)
            fistInput = Vector2.up;

        // Passa al DynamicMoveProvider il nostro input invece di quello dei controller
        return base.ComputeDesiredMove(fistInput);
    }

    bool IsPalmUp(XRHand hand)
    {
        if (!hand.isTracked) return false;

        try
        {
            // Controlla che il palmo sia rivolto verso l'alto
            // usando il joint del palmo
            var palmJoint = hand.GetJoint(XRHandJointID.Palm);
            if (!palmJoint.TryGetPose(out Pose palmPose)) return false;

            // Il palmo è rivolto verso l'alto se il suo "up" punta verso il cielo
            float dot = Vector3.Dot(palmPose.rotation * Vector3.down, Vector3.up);
            return dot > 0.7f; // circa 45 gradi di tolleranza
        }
        catch
        {
            return false;
        }
    }
}