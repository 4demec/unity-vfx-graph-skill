// VfxAiPreviewMover.cs
// Moves a preview object so motion-dependent effects (trails above all) can actually be judged.
// Runs in edit mode via [ExecuteAlways], so you don't have to enter play mode to see a trail.

using UnityEngine;
using UnityEngine.Serialization;

[ExecuteAlways]
[AddComponentMenu("VFX AI/Preview Mover")]
public class VfxAiPreviewMover : MonoBehaviour
{
    public enum Motion
    {
        Orbit,
        Figure8,
        PingPong,
        Spiral,
    }

    public Motion motion = Motion.Orbit;
    public Vector3 center = Vector3.zero;
    public float radius = 3f;
    public float speed = 1f;
    public float height = 1f;
    public bool faceMotion = true;

    // Named to avoid shadowing MonoBehaviour.runInEditMode, which is a real Unity property.
    // FormerlySerializedAs keeps existing preview objects working after the rename.
    [FormerlySerializedAs("runInEditMode")]
    public bool animateInEditMode = true;

    Vector3 m_LastPosition;
    bool m_HasLast;

    void OnEnable()
    {
        m_LastPosition = transform.position;
        m_HasLast = false;
    }

    void Update()
    {
        if (!Application.isPlaying && !animateInEditMode) return;

        var t = Now() * speed;
        Vector3 p;

        switch (motion)
        {
            case Motion.Figure8:
                p = new Vector3(Mathf.Sin(t) * radius, Mathf.Sin(t * 2f) * height * 0.5f, Mathf.Sin(t * 2f) * radius * 0.5f);
                break;

            case Motion.PingPong:
                p = new Vector3(Mathf.Sin(t) * radius, 0f, 0f);
                break;

            case Motion.Spiral:
                p = new Vector3(Mathf.Cos(t) * radius, Mathf.Repeat(t * 0.35f, 1f) * height * 2f - height, Mathf.Sin(t) * radius);
                break;

            default: // Orbit
                p = new Vector3(Mathf.Cos(t) * radius, height, Mathf.Sin(t) * radius);
                break;
        }

        p += center;
        transform.position = p;

        if (faceMotion && m_HasLast)
        {
            var delta = p - m_LastPosition;
            if (delta.sqrMagnitude > 1e-8f)
                transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        }

        m_LastPosition = p;
        m_HasLast = true;
    }

    static float Now()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            return (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
        return Time.time;
    }
}
