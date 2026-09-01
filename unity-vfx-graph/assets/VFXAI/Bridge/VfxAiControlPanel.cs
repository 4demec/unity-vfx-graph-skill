// VfxAiControlPanel.cs
// The approval surface. Nothing that modifies project assets runs until it is approved here
// (or until auto-approve is deliberately switched on).

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VfxAi.Bridge
{
    public class VfxAiControlPanel : EditorWindow
    {
        Vector2 m_Scroll;

        [MenuItem("Tools/VFX AI/Control Panel")]
        public static void Open()
        {
            var w = GetWindow<VfxAiControlPanel>(false, "VFX AI", true);
            w.minSize = new Vector2(420, 260);
            w.Show();
        }

        public static void RepaintAll()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<VfxAiControlPanel>())
                w.Repaint();
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        double m_NextRepaint;

        void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < m_NextRepaint) return;
            m_NextRepaint = EditorApplication.timeSinceStartup + 1.0;
            Repaint();
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Bridge", EditorStyles.boldLabel);
                VfxAiJobRunner.bridgeEnabled = EditorGUILayout.ToggleLeft(
                    "Watch for AI jobs", VfxAiJobRunner.bridgeEnabled);

                using (new EditorGUI.DisabledScope(!VfxAiJobRunner.bridgeEnabled))
                {
                    var newAuto = EditorGUILayout.ToggleLeft(
                        "Auto-approve asset changes (no prompt)", VfxAiJobRunner.autoApprove);
                    if (newAuto != VfxAiJobRunner.autoApprove)
                    {
                        if (!newAuto || EditorUtility.DisplayDialog("Auto-approve VFX AI changes?",
                                "Asset-modifying jobs will run without asking. You can still undo in the editor, "
                                + "but nothing will pause for review.\n\nTurn auto-approve on?",
                                "Turn on", "Cancel"))
                        {
                            VfxAiJobRunner.autoApprove = newAuto;
                        }
                    }
                }

                EditorGUILayout.LabelField("Unity", Application.unityVersion);
                EditorGUILayout.LabelField("Jobs folder", VfxAiJobRunner.jobsDir);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open jobs folder"))
                        EditorUtility.RevealInFinder(VfxAiJobRunner.jobsDir);
                    if (GUILayout.Button("Open results folder"))
                        EditorUtility.RevealInFinder(VfxAiJobRunner.resultsDir);
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Pending approval", EditorStyles.boldLabel);

            var jobs = VfxAiJobRunner.pending;
            if (jobs.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing waiting. Asset-modifying jobs from the AI will queue up here.",
                    MessageType.None);
                return;
            }

            // Oldest first: jobs against one asset build on each other, so that is the order they
            // are meant to be read and approved in.
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var blocker = VfxAiJobRunner.BlockedBy(job);

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(job.fileName, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("op", job.op);
                    if (!string.IsNullOrEmpty(job.target)) EditorGUILayout.LabelField("target", job.target);
                    EditorGUILayout.LabelField("queued", job.seenUtc.ToLocalTime().ToString("HH:mm:ss"));

                    if (blocker != null)
                    {
                        EditorGUILayout.HelpBox(
                            "Waiting on " + blocker.fileName + ", which targets the same asset and was queued first.\n"
                            + "Later jobs assume the earlier one has been applied, so they must go in order.",
                            MessageType.Info);
                    }

                    var preview = job.args ?? string.Empty;
                    if (preview.Length > 4000) preview = preview.Substring(0, 4000) + "\n... (truncated)";
                    EditorGUILayout.LabelField("payload");
                    EditorGUILayout.SelectableLabel(preview,
                        EditorStyles.textArea,
                        GUILayout.MinHeight(60), GUILayout.MaxHeight(220));

                    using (new EditorGUI.DisabledScope(blocker != null))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f);
                        if (GUILayout.Button("Approve"))
                        {
                            GUI.backgroundColor = Color.white;
                            VfxAiJobRunner.Approve(job);
                            GUIUtility.ExitGUI();
                        }
                        GUI.backgroundColor = new Color(0.9f, 0.6f, 0.6f);
                        if (GUILayout.Button("Reject"))
                        {
                            GUI.backgroundColor = Color.white;
                            VfxAiJobRunner.Reject(job, "rejected from the control panel");
                            GUIUtility.ExitGUI();
                        }
                        GUI.backgroundColor = Color.white;
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
