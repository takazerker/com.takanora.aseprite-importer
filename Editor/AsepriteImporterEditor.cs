// Copyright 2022 Takanori Shibasaki
//  
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.
//
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.AssetImporters;

namespace Aseprite
{
    class LayerList : TreeView
    {
        string[] m_LayerNames;
        SerializedProperty m_LayerArray;

        public LayerList(string[] layerNames, SerializedProperty layerArray, TreeViewState state, MultiColumnHeader header) : base(state, header)
        {
            m_LayerNames = layerNames;
            m_LayerArray = layerArray;

            showBorder = true;
            rowHeight = EditorGUIUtility.singleLineHeight;
        }

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem(-1, -1);

            for (var i = 0; i < m_LayerNames.Length; ++i)
            {
                var item = new TreeViewItem(i, -1, m_LayerNames[i]);
                root.AddChild(item);
            }

            SetupDepthsFromParentsAndChildren(root);

            return root;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            var entryIndex = FindEnabledLayerIndex(args.item.displayName);
            var enabled = 0 <= entryIndex;

            var nameRect = args.GetCellRect(0);
            nameRect.xMin += 1;

            var toggleRect = nameRect;
            toggleRect.width = 18;
            nameRect.xMin = toggleRect.xMax;

            EditorGUI.BeginChangeCheck();

            enabled = EditorGUI.Toggle(toggleRect, enabled);
            EditorGUI.LabelField(nameRect, args.item.displayName);

            if (EditorGUI.EndChangeCheck())
            {
                if (enabled)
                {
                    m_LayerArray.InsertArrayElementAtIndex(m_LayerArray.arraySize);

                    var elem = m_LayerArray.GetArrayElementAtIndex(m_LayerArray.arraySize - 1);
                    elem.FindPropertyRelative(nameof(LayerPositionSetting.LayerName)).stringValue = args.item.displayName;
                    elem.FindPropertyRelative(nameof(LayerPositionSetting.Pivot)).vector2Value = new Vector2(0.5f, 0.5f);
                }
                else
                {
                    m_LayerArray.DeleteArrayElementAtIndex(entryIndex);
                    entryIndex = -1;
                }
            }

            if (0 <= entryIndex)
            {
                var pivot = m_LayerArray.GetArrayElementAtIndex(entryIndex).FindPropertyRelative(nameof(LayerPositionSetting.Pivot));
                var x = pivot.FindPropertyRelative("x");
                var y = pivot.FindPropertyRelative("y");

                var pivotRect = args.GetCellRect(1);
                pivotRect.width = pivotRect.width * 0.5f - 3;

                x.floatValue = EditorGUI.FloatField(pivotRect, x.floatValue);

                pivotRect.x += pivotRect.width + 3;
                y.floatValue = EditorGUI.FloatField(pivotRect, y.floatValue);
            }
        }

        int FindEnabledLayerIndex(string name)
        {
            for (var i = 0; i < m_LayerArray.arraySize; ++i)
            {
                var elem = m_LayerArray.GetArrayElementAtIndex(i);
                if (elem.FindPropertyRelative(nameof(LayerPositionSetting.LayerName)).stringValue == name)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    [CustomEditor(typeof(AsepriteImporter))]
    [CanEditMultipleObjects]
    class AsepriteImporterEditor : ScriptedImporterEditor
    {
        [SerializeField]
        TreeViewState m_LayerListState = new TreeViewState();

        [SerializeField]
        MultiColumnHeaderState m_LayerListColumnHeaderState;

        LayerList m_LayerList;
        AseFile m_TargetFile;

        string[] m_LayerNames;

        public override bool showImportedObject => false;

        protected override void Awake()
        {
            base.Awake();

            MultiColumnHeaderState.Column[] columns = new MultiColumnHeaderState.Column[2];

            columns[0] = new MultiColumnHeaderState.Column();
            columns[0].width = 150;
            columns[1] = new MultiColumnHeaderState.Column();
            columns[1].width = 100;

            m_LayerListColumnHeaderState = new MultiColumnHeaderState(columns);
        }

        public override void OnEnable()
        {
            base.OnEnable();

            if (targets.Length == 1)
            {
                var targetPath = (target as AssetImporter).assetPath;
                m_TargetFile = new AseFile(targetPath);

                m_LayerNames = new string[m_TargetFile.Frames[0].Cels.Count];

                for (var i = 0; i < m_TargetFile.Frames[0].Cels.Count; ++i)
                {
                    m_LayerNames[i] = m_TargetFile.Frames[0].Cels[i].Layer.Name;
                }

                var columns = m_LayerListColumnHeaderState.columns;

                columns[0].headerContent = new GUIContent("Layer");
                columns[0].maxWidth = float.MaxValue;
                columns[0].minWidth = 10;
                columns[0].autoResize = true;
                columns[0].canSort = false;

                columns[1].headerContent = new GUIContent("Pivot");
                columns[1].maxWidth = float.MaxValue;
                columns[1].minWidth = 10;
                columns[1].autoResize = false;
                columns[1].canSort = false;

                m_LayerList = new LayerList(m_LayerNames, serializedObject.FindProperty(nameof(AsepriteImporter.GenerateLayerPositionCurves)), m_LayerListState, new MultiColumnHeader(m_LayerListColumnHeaderState));
                m_LayerList.Reload();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var itr = serializedObject.GetIterator();
            itr.NextVisible(true);

            while (itr.NextVisible(false))
            {
                if (itr.name == nameof(AsepriteImporter.GenerateLayerPositionCurves))
                {
                    if (m_LayerList != null)
                    {
                        EditorGUILayout.Space();
                        EditorGUILayout.LabelField("Generate Layer Position Curves", EditorStyles.boldLabel);
                        m_LayerList.OnGUI(GUILayoutUtility.GetRect(1, 100));
                    }
                }
                else
                {
                    EditorGUILayout.PropertyField(itr);
                }
            }

            serializedObject.ApplyModifiedProperties();

            ApplyRevertGUI();
        }

        protected override bool OnApplyRevertGUI()
        {
            return base.OnApplyRevertGUI();
        }

    }
}
