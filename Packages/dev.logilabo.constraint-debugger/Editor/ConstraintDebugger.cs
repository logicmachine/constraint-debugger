using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.Dynamics.ManagedTypes;

namespace dev.logilabo.constraint_debugger.editor
{
    public class ConstraintDebugger : EditorWindow
    {
        [MenuItem("Window/Logilabo/Constraint Debugger")]
        private static void ShowWindow()
        {
            var window = GetWindow<ConstraintDebugger>();
            window.Show();
        }

        private object GetConstraintGrouperObject()
        {
            // static class VRCConstraintManager {
            //     private static VRCConstraintGrouper _constraintGrouper;
            // }
            var type = typeof(VRCConstraintManager);
            var field = type.GetField("_constraintGrouper", BindingFlags.NonPublic | BindingFlags.Static);
            return field.GetValue(null);
        }

        private object GetExecutionGroupsObject(object grouper)
        {
            // internal class VRCConstraintGrouper {
            //     public SortedDictionary<int, VRCConstraintGroup> ExecutionGroups { get { ... } }
            // }
            var property = grouper.GetType().GetProperty("ExecutionGroups");
            return property.GetValue(grouper);
        }

        private SortedDictionary<int, object> ConvertExecutionGroupsObject(object groups)
        {
            var result = new SortedDictionary<int, object>();
            var enumerator = groups.GetType().GetMethod("GetEnumerator").Invoke(groups, null);
            var moveNextMethod = enumerator.GetType().GetMethod("MoveNext");
            var currentProperty = enumerator.GetType().GetProperty("Current");
            while ((bool)moveNextMethod.Invoke(enumerator, null))
            {
                var item = currentProperty.GetValue(enumerator);
                var key = (int)item.GetType().GetProperty("Key").GetValue(item);
                var value = item.GetType().GetProperty("Value").GetValue(item);
                result.Add(key, value);
            }
            return result;
        }

        private IDictionary<int, VRCConstraintBase> GetManagedConstraints()
        {
            // static class VRCConstraintManager {
            //     private static List<VRCConstraintBase> _constraintsManaged;
            // }
            var type = typeof(VRCConstraintManager);
            var field = type.GetField("_constraintsManaged", BindingFlags.NonPublic | BindingFlags.Static);
            var list = (List<VRCConstraintBase>)field.GetValue(null);
            var result = new SortedDictionary<int, VRCConstraintBase>();
            foreach (var item in list)
            {
                // public abstract class VRCConstraintBase {
                //     internal int NativeIndex { get; set; }
                // }
                var ni = (int)item.GetType().GetProperty("NativeIndex", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(item);
                result.Add(ni, item);
            }
            return result;
        }

        private IReadOnlyList<IReadOnlyList<VRCConstraintBase>> GetExecutionGroups()
        {
            var groups = ConvertExecutionGroupsObject(GetExecutionGroupsObject(GetConstraintGrouperObject()));
            var constraints = GetManagedConstraints();
            var result = new List<IReadOnlyList<VRCConstraintBase>>();
            foreach (var group in groups)
            {
                while (result.Count <= group.Key) { result.Add(null); }
                // internal class VRCConstraintGroup {
                //     public UnsafeList<int> MemberConstraintIndices;
                // }
                var value = group.Value;
                var indices = (UnsafeList<int>)value.GetType().GetField("MemberConstraintIndices").GetValue(value);
                var list = new List<VRCConstraintBase>();
                foreach (var i in indices) { list.Add(constraints[i]); }
                result[group.Key] = list;
            }
            return result;
        }

        private string GenerateDotGraph()
        {
            var scene = SceneManager.GetActiveScene();
            var builder = new StringBuilder();
            builder.Append("digraph {\n");
            
            // Enumerate constraints
            var constraints = new List<VRCConstraintBase>();
            foreach (var root in scene.GetRootGameObjects())
            {
                constraints.AddRange(root
                    .GetComponentsInChildren<VRCConstraintBase>(false)
                    .Where(c => c.enabled));
            }
            
            // Enumerate constraint-related objects
            var nodeSet = new HashSet<GameObject>();
            foreach (var constraint in constraints)
            {
                nodeSet.Add(constraint.GetEffectiveTargetTransform().gameObject);
                foreach (var source in constraint.Sources)
                {
                    if (source.SourceTransform == null) { continue; }
                    nodeSet.Add(source.SourceTransform.gameObject);
                }
                if (constraint is VRCWorldUpConstraintBase wu)
                {
                    if (wu.WorldUpTransform != null) { nodeSet.Add(wu.WorldUpTransform.gameObject); }
                }
            }

            // Enumerate ancestors
            var nodes = new List<GameObject>(nodeSet);
            for (var i = 0; i < nodes.Count; ++i)
            {
                var child = nodes[i];
                if (child.transform.parent == null) { continue; }
                var parent = child.transform.parent.gameObject;
                if (nodeSet.Add(parent)) { nodes.Add(parent); }
            }

            // Draw nodes
            foreach (var node in nodes)
            {
                builder.Append($"{node.GetInstanceID()} [label=\"{node.name}\"]\n");
            }
            
            // Draw edges from hierarchy
            foreach (var child in nodes)
            {
                if (child.transform.parent == null) { continue; }
                var parent = child.transform.parent.gameObject;
                builder.Append($"{parent.GetInstanceID()} -> {child.GetInstanceID()} [color=\"gray\"]\n");
            }

            // Draw edges from constraints
            foreach (var constraint in constraints)
            {
                var target = constraint.GetEffectiveTargetTransform().gameObject;
                foreach (var source in constraint.Sources)
                {
                    if (source.SourceTransform == null) { continue; }
                    var s = source.SourceTransform.gameObject;
                    builder.Append($"{s.GetInstanceID()} -> {target.GetInstanceID()} [color=\"orangered\"]\n");
                }
            }

            // Finalize
            builder.Append("}\n");
            return builder.ToString();
        }
        
        private Vector2 _scroll = new Vector2(0, 0);

        private IReadOnlyList<IReadOnlyList<VRCConstraintBase>> _lastExecutionGroups;
        private readonly IList<bool> _executionGroupsFoldouts = new List<bool>();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            
            //------------------------------------------------------------
            // Execution Groups
            //-------------------------------------------------------------
            EditorGUILayout.LabelField("Execution Groups", EditorStyles.boldLabel);
            if (GUILayout.Button("Reload"))
            {
                var groups = GetExecutionGroups();
                _lastExecutionGroups = groups;
                while (_executionGroupsFoldouts.Count < groups.Count) { _executionGroupsFoldouts.Add(false); }
            }
            if (_lastExecutionGroups != null)
            {
                for (var i = 0; i < _lastExecutionGroups.Count; ++i)
                {
                    var group = _lastExecutionGroups[i];
                    _executionGroupsFoldouts[i] = EditorGUILayout.Foldout(
                        _executionGroupsFoldouts[i], $"Group {i}");
                    if (_executionGroupsFoldouts[i] && group != null)
                    {
                        ++EditorGUI.indentLevel;
                        foreach (var item in group)
                        {
                            EditorGUILayout.ObjectField(item, item.GetType(), true);
                        }
                        --EditorGUI.indentLevel;
                    }
                }
            }
            EditorGUILayout.Space();
            
            //------------------------------------------------------------
            // Dependency Visualizer
            //-------------------------------------------------------------
            EditorGUILayout.LabelField("Dependency Visualizer", EditorStyles.boldLabel);
            if (GUILayout.Button("Export"))
            {
                var graph = GenerateDotGraph();
                var path = EditorUtility.SaveFilePanel(
                    "Save dependency graph", "", "graph.dot", "dot");
                if (!string.IsNullOrEmpty(path)) { File.WriteAllText(path, graph); }
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
