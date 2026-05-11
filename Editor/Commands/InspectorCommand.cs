using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Inspector operations: component and SerializedProperty read/write.
    /// Supports multiple sub-commands via "action" parameter.
    /// </summary>
    public class InspectorCommand : ICommand
    {
        private const string AssetsPathPrefix = "Assets/";
        private const string PackagesPathPrefix = "Packages/";
        private const string PrefabExtension = ".prefab";

        public string Type => "inspector";
        public bool RequiresRefresh => true;

        public string SkillDescription => @"### `inspector` - Serialized Component/Asset Properties

```bash
$CLI inspector get_components --path ""Player""
$CLI inspector get_properties --path ""Player"" --componentName ""Transform""
$CLI inspector set_property --path ""Player"" --componentName ""Rigidbody"" --propertyName ""mass"" --value 10
$CLI inspector set_property --path ""Player"" --componentName ""MeshRenderer"" --propertyName ""m_Materials.Array.data[0]"" --value ""Assets/Materials/MyMat.mat""
$CLI inspector get_components --assetPath ""Assets/UI/LoginPanel.prefab"" --objectPath ""Root/Button""
$CLI inspector set_property --assetPath ""Assets/UI/LoginPanel.prefab"" --objectPath ""Root/Button"" --componentName ""RectTransform"" --propertyName ""m_AnchoredPosition.x"" --value 100
$CLI inspector set_properties --json '{ ""assetPath"": ""Assets/UI/LoginPanel.prefab"", ""objectPath"": ""Root/Button"", ""componentName"": ""RectTransform"", ""values"": { ""m_AnchoredPosition.x"": 100, ""m_AnchoredPosition.y"": -40, ""m_LocalPosition.z"": 0 } }'
$CLI inspector set_property --assetPath ""Assets/Data/Config.asset"" --propertyName ""maxCount"" --value 20
$CLI inspector add_component --path ""Player"" --typeName ""Rigidbody""
$CLI inspector remove_component --path ""Player"" --componentName ""Rigidbody""
```

Use `assetPath + objectPath` for prefab asset editing. Prefer SerializedProperty paths over YAML text edits; YAML patching should only be a last-resort dry-run workflow.";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "get_components");

            try
            {
                switch (action.ToLower())
                {
                    case "get_components":
                        return GetComponents(request);
                    case "get_properties":
                        return GetProperties(request);
                    case "get_property":
                        return GetProperty(request);
                    case "find_property":
                        return FindProperty(request);
                    case "set_property":
                        return SetProperty(request);
                    case "set_properties":
                        return SetProperties(request);
                    case "add_component":
                        return AddComponent(request);
                    case "remove_component":
                        return RemoveComponent(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: get_components, get_properties, get_property, find_property, set_property, set_properties, add_component, remove_component");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult GetComponents(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                var go = context.GameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "Target does not contain components");
                }

                var components = new List<ComponentInfo>();
                var allComponents = go.GetComponents<Component>();

                for (var i = 0; i < allComponents.Length; i++)
                {
                    var comp = allComponents[i];
                    if (comp == null) continue;

                    var info = new ComponentInfo
                    {
                        index = i,
                        typeName = comp.GetType().Name,
                        fullTypeName = comp.GetType().FullName,
                        instanceId = comp.GetInstanceID()
                    };

                    if (comp is Behaviour behaviour)
                    {
                        info.enabled = behaviour.enabled;
                    }

                    components.Add(info);
                }

                return CommandResult.Success(request.id, new
                {
                    gameObjectName = go.name,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    components = components,
                    count = components.Count
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult GetProperties(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                var includeChildren = request.GetParam("includeChildren", false);
                var properties = new List<PropertyInfo>();
                var so = new SerializedObject(serializedTarget);
                var iterator = so.GetIterator();
                var enterChildren = true;

                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = includeChildren;
                    properties.Add(BuildPropertyInfo(iterator));
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    gameObjectName = context.GameObject != null ? context.GameObject.name : null,
                    componentName = component != null ? component.GetType().Name : null,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    properties = properties
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult GetProperty(CommandRequest request)
        {
            var propertyName = request.GetParam<string>("propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                return CommandResult.Failure(request.id, "Missing 'propertyName' parameter");
            }

            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                var so = new SerializedObject(serializedTarget);
                var prop = so.FindProperty(propertyName);
                if (prop == null)
                {
                    return CommandResult.Failure(request.id, $"Property not found: {propertyName}");
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    componentName = component != null ? component.GetType().Name : null,
                    propertyName = propertyName,
                    propertyType = prop.propertyType.ToString(),
                    value = GetPropertyValue(prop),
                    editable = prop.editable,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult FindProperty(CommandRequest request)
        {
            var keyword = request.GetParam<string>("keyword", null);
            if (string.IsNullOrEmpty(keyword))
            {
                return CommandResult.Failure(request.id, "Missing 'keyword' parameter");
            }

            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                var matches = new List<PropertyInfo>();
                var comparison = StringComparison.OrdinalIgnoreCase;
                var so = new SerializedObject(serializedTarget);
                var iterator = so.GetIterator();
                var enterChildren = true;

                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = true;
                    if (iterator.propertyPath.IndexOf(keyword, comparison) < 0
                        && iterator.name.IndexOf(keyword, comparison) < 0
                        && iterator.displayName.IndexOf(keyword, comparison) < 0)
                    {
                        continue;
                    }

                    matches.Add(BuildPropertyInfo(iterator));
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    componentName = component != null ? component.GetType().Name : null,
                    keyword = keyword,
                    count = matches.Count,
                    matches = matches,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult SetProperty(CommandRequest request)
        {
            var propertyName = request.GetParam<string>("propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                return CommandResult.Failure(request.id, "Missing 'propertyName' parameter");
            }

            var values = new Dictionary<string, object>();
            values[propertyName] = request.GetParam<object>("value", null);
            return SetPropertiesInternal(request, values);
        }

        private CommandResult SetProperties(CommandRequest request)
        {
            Dictionary<string, object> values;
            string error;
            if (!TryGetValuesDictionary(request, out values, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            return SetPropertiesInternal(request, values);
        }

        private CommandResult SetPropertiesInternal(CommandRequest request, Dictionary<string, object> values)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, true, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                if (serializedTarget == null)
                {
                    return CommandResult.Failure(request.id, "Serialized target not found");
                }

                var so = new SerializedObject(serializedTarget);
                so.Update();

                if (context.IsSceneObject)
                {
                    Undo.RecordObject(serializedTarget, "Set Serialized Properties");
                }

                var changes = new List<PropertyChangeInfo>();
                foreach (var pair in values)
                {
                    if (string.IsNullOrEmpty(pair.Key))
                    {
                        return CommandResult.Failure(request.id, "Property name cannot be empty");
                    }

                    var prop = so.FindProperty(pair.Key);
                    if (prop == null)
                    {
                        return CommandResult.Failure(request.id, $"Property not found: {pair.Key}");
                    }

                    if (!prop.editable)
                    {
                        return CommandResult.Failure(request.id, $"Property is not editable: {pair.Key}");
                    }

                    var oldValue = GetPropertyValue(prop);
                    if (!SetPropertyValue(prop, pair.Value))
                    {
                        return CommandResult.Failure(request.id, $"Failed to set property value for '{pair.Key}' ({prop.propertyType})");
                    }

                    changes.Add(new PropertyChangeInfo
                    {
                        propertyName = pair.Key,
                        propertyType = prop.propertyType.ToString(),
                        oldValue = oldValue,
                        newValue = GetPropertyValue(prop)
                    });
                }

                var changed = so.ApplyModifiedProperties();
                if (changed)
                {
                    SaveModifiedTarget(context, serializedTarget);
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    gameObjectName = context.GameObject != null ? context.GameObject.name : null,
                    componentName = component != null ? component.GetType().Name : null,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    changed = changed,
                    changes = changes
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult AddComponent(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, true, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                var go = context.GameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "Target does not contain components");
                }

                var typeName = request.GetParam<string>("typeName");
                if (string.IsNullOrEmpty(typeName))
                {
                    return CommandResult.Failure(request.id, "Missing 'typeName' parameter");
                }

                var componentType = ResolveComponentType(typeName);
                if (componentType == null)
                {
                    return CommandResult.Failure(request.id, $"Component type not found: {typeName}");
                }

                if (!typeof(Component).IsAssignableFrom(componentType))
                {
                    return CommandResult.Failure(request.id, $"Type is not a Component: {typeName}");
                }

                Component newComponent;
                if (context.IsSceneObject)
                {
                    newComponent = Undo.AddComponent(go, componentType);
                }
                else
                {
                    newComponent = go.AddComponent(componentType);
                    SaveModifiedTarget(context, newComponent);
                }

                return CommandResult.Success(request.id, new
                {
                    gameObjectName = go.name,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    addedComponent = newComponent.GetType().Name,
                    instanceId = newComponent.GetInstanceID()
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult RemoveComponent(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, true, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                var go = context.GameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "Target does not contain components");
                }

                var component = ResolveComponent(context, request);
                if (component == null)
                {
                    return CommandResult.Failure(request.id, "Component not found");
                }

                if (component is Transform)
                {
                    return CommandResult.Failure(request.id, "Cannot remove Transform component");
                }

                var removedTypeName = component.GetType().Name;
                if (context.IsSceneObject)
                {
                    Undo.DestroyObjectImmediate(component);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(component, true);
                    SaveModifiedTarget(context, go);
                }

                return CommandResult.Success(request.id, new
                {
                    gameObjectName = go.name,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    removedComponent = removedTypeName
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private bool TryResolveTargetContext(CommandRequest request, bool forWrite, out TargetContext context, out string error)
        {
            context = null;
            error = null;

            var assetPath = request.GetParam<string>("assetPath", null);
            var objectPath = request.GetParam<string>("objectPath", null);

            if (!string.IsNullOrEmpty(assetPath))
            {
                if (!IsUnityAssetPath(assetPath))
                {
                    error = "assetPath must start with Assets/ or Packages/";
                    return false;
                }

                if (forWrite && assetPath.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Editing package assets is not supported. Copy the asset into Assets/ first.";
                    return false;
                }

                if (Path.GetExtension(assetPath).Equals(PrefabExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return TryResolvePrefabAssetContext(assetPath, objectPath, out context, out error);
                }

                if (!string.IsNullOrEmpty(objectPath))
                {
                    error = "objectPath is only supported for prefab assets";
                    return false;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null)
                {
                    error = $"Asset not found at path: {assetPath}";
                    return false;
                }

                var assetGameObject = asset as GameObject;
                context = new TargetContext
                {
                    AssetPath = assetPath,
                    SerializedTarget = asset,
                    GameObject = assetGameObject,
                    IsSceneObject = false,
                    IsAssetObject = true
                };
                return true;
            }

            var componentInstanceId = request.GetParam("componentInstanceId", 0);
            if (componentInstanceId != 0)
            {
                var component = GetObjectByInstanceId(componentInstanceId) as Component;
                if (component == null)
                {
                    error = $"Component not found by instanceId: {componentInstanceId}";
                    return false;
                }

                context = new TargetContext
                {
                    SerializedTarget = component,
                    GameObject = component.gameObject,
                    IsSceneObject = true
                };
                return true;
            }

            var go = GetSceneGameObject(request);
            if (go == null)
            {
                error = "GameObject not found";
                return false;
            }

            context = new TargetContext
            {
                GameObject = go,
                SerializedTarget = go,
                IsSceneObject = true
            };
            return true;
        }

        private bool TryResolvePrefabAssetContext(string assetPath, string objectPath, out TargetContext context, out string error)
        {
            context = null;
            error = null;

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
            }
            catch (Exception ex)
            {
                error = $"Failed to load prefab contents: {ex.Message}";
                return false;
            }

            if (root == null)
            {
                error = $"Prefab not found at path: {assetPath}";
                return false;
            }

            var target = ResolvePrefabObject(root, objectPath);
            if (target == null)
            {
                PrefabUtility.UnloadPrefabContents(root);
                error = $"Object not found in prefab: {objectPath}";
                return false;
            }

            context = new TargetContext
            {
                AssetPath = assetPath,
                ObjectPath = string.IsNullOrEmpty(objectPath) ? target.name : objectPath,
                PrefabRoot = root,
                GameObject = target,
                SerializedTarget = target,
                IsPrefabAsset = true,
                IsAssetObject = true,
                IsSceneObject = false
            };
            return true;
        }

        private bool TryResolveSerializedTarget(TargetContext context, CommandRequest request, out UnityEngine.Object serializedTarget, out Component component, out string error)
        {
            serializedTarget = null;
            component = null;
            error = null;

            if (context == null)
            {
                error = "Target context is null";
                return false;
            }

            if (context.SerializedTarget is Component existingComponent)
            {
                component = existingComponent;
                serializedTarget = existingComponent;
                return true;
            }

            if (context.GameObject != null)
            {
                component = ResolveComponent(context, request);
                if (component == null)
                {
                    error = "Component not found. Provide 'componentName' or 'componentIndex'";
                    return false;
                }

                serializedTarget = component;
                return true;
            }

            if (context.SerializedTarget != null)
            {
                if (!string.IsNullOrEmpty(request.GetParam<string>("componentName", null)) || request.GetParam("componentIndex", -1) >= 0)
                {
                    error = "componentName/componentIndex can only be used with GameObject targets";
                    return false;
                }

                serializedTarget = context.SerializedTarget;
                return true;
            }

            error = "Serialized target not found";
            return false;
        }

        private Component ResolveComponent(TargetContext context, CommandRequest request)
        {
            var go = context != null ? context.GameObject : null;
            if (go == null)
            {
                return null;
            }

            var componentInstanceId = request.GetParam("componentInstanceId", 0);
            if (componentInstanceId != 0 && context.IsSceneObject)
            {
                var componentById = GetObjectByInstanceId(componentInstanceId) as Component;
                if (componentById != null && componentById.gameObject == go)
                {
                    return componentById;
                }
            }

            var componentIndex = request.GetParam("componentIndex", -1);
            if (componentIndex >= 0)
            {
                var components = go.GetComponents<Component>();
                if (componentIndex < components.Length)
                {
                    return components[componentIndex];
                }

                return null;
            }

            var componentName = request.GetParam<string>("componentName", null);
            if (string.IsNullOrEmpty(componentName))
            {
                return null;
            }

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null && (comp.GetType().Name == componentName || comp.GetType().FullName == componentName))
                {
                    return comp;
                }
            }

            return null;
        }

        private GameObject GetSceneGameObject(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var instanceId = request.GetParam("instanceId", 0);

            if (instanceId != 0)
            {
                return GetObjectByInstanceId(instanceId) as GameObject;
            }

            if (!string.IsNullOrEmpty(path))
            {
                return GameObject.Find(path);
            }

            return Selection.activeGameObject;
        }

        private static UnityEngine.Object GetObjectByInstanceId(int instanceId)
        {
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(instanceId);
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }

        private static bool IsUnityAssetPath(string assetPath)
        {
            return assetPath.StartsWith(AssetsPathPrefix, StringComparison.OrdinalIgnoreCase)
                   || assetPath.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static GameObject ResolvePrefabObject(GameObject root, string objectPath)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(objectPath) || objectPath == "." || objectPath == "/" || objectPath == root.name)
            {
                return root;
            }

            var normalized = objectPath.Replace('\\', '/').Trim('/');
            if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(root.name.Length + 1);
            }

            var child = root.transform.Find(normalized);
            if (child != null)
            {
                return child.gameObject;
            }

            // Transform.Find 需要完整路径；这里提供按名称兜底，便于 AI 在只知道对象名时定位。
            return FindChildByName(root.transform, normalized);
        }

        private static GameObject FindChildByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == name)
                {
                    return child.gameObject;
                }

                var found = FindChildByName(child, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void SaveModifiedTarget(TargetContext context, UnityEngine.Object serializedTarget)
        {
            if (context == null)
            {
                return;
            }

            if (context.IsPrefabAsset && context.PrefabRoot != null)
            {
                PrefabUtility.SaveAsPrefabAsset(context.PrefabRoot, context.AssetPath);
                AssetDatabase.ImportAsset(context.AssetPath);
                return;
            }

            if (context.IsAssetObject && serializedTarget != null)
            {
                EditorUtility.SetDirty(serializedTarget);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(context.AssetPath);
            }
        }

        private static Type ResolveComponentType(string typeName)
        {
            var possibleNames = new[]
            {
                typeName,
                $"UnityEngine.{typeName}",
                $"UnityEngine.UI.{typeName}",
                $"TMPro.{typeName}"
            };

            foreach (var name in possibleNames)
            {
                var componentType = System.Type.GetType(name);
                if (componentType != null)
                {
                    return componentType;
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    componentType = assembly.GetType(name);
                    if (componentType != null)
                    {
                        return componentType;
                    }
                }
            }

            return null;
        }

        private bool TryGetValuesDictionary(CommandRequest request, out Dictionary<string, object> values, out string error)
        {
            values = null;
            error = null;

            var rawValues = request.GetParam<object>("values", null);
            if (rawValues == null)
            {
                error = "Missing 'values' parameter";
                return false;
            }

            if (rawValues is Dictionary<string, object> typedValues)
            {
                values = typedValues;
                if (values.Count > 0)
                {
                    return true;
                }

                error = "Parameter 'values' cannot be empty";
                return false;
            }

            if (rawValues is IDictionary dictionary)
            {
                values = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key == null)
                    {
                        continue;
                    }

                    values[entry.Key.ToString()] = entry.Value;
                }

                if (values.Count > 0)
                {
                    return true;
                }

                error = "Parameter 'values' cannot be empty";
                return false;
            }

            var valuesText = rawValues as string;
            if (!string.IsNullOrEmpty(valuesText))
            {
                try
                {
                    values = AIBridgeJson.DeserializeObject(valuesText);
                    if (values != null && values.Count > 0)
                    {
                        return true;
                    }

                    error = "Parameter 'values' cannot be empty";
                    return false;
                }
                catch (Exception ex)
                {
                    error = $"Invalid values JSON: {ex.Message}";
                    return false;
                }
            }

            error = "Parameter 'values' must be a JSON object";
            return false;
        }

        private PropertyInfo BuildPropertyInfo(SerializedProperty prop)
        {
            return new PropertyInfo
            {
                name = prop.name,
                propertyPath = prop.propertyPath,
                displayName = prop.displayName,
                propertyType = prop.propertyType.ToString(),
                editable = prop.editable,
                isExpanded = prop.isExpanded,
                hasChildren = prop.hasChildren,
                depth = prop.depth,
                value = GetPropertyValue(prop)
            };
        }

        private object GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return prop.floatValue;
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    return new { r = prop.colorValue.r, g = prop.colorValue.g, b = prop.colorValue.b, a = prop.colorValue.a };
                case SerializedPropertyType.ObjectReference:
                    var objectReference = prop.objectReferenceValue;
                    if (objectReference == null)
                    {
                        return null;
                    }
                    return new
                    {
                        name = objectReference.name,
                        type = objectReference.GetType().Name,
                        instanceId = objectReference.GetInstanceID(),
                        assetPath = AssetDatabase.GetAssetPath(objectReference)
                    };
                case SerializedPropertyType.Enum:
                    return prop.enumNames[prop.enumValueIndex];
                case SerializedPropertyType.Vector2:
                    return new { x = prop.vector2Value.x, y = prop.vector2Value.y };
                case SerializedPropertyType.Vector3:
                    return new { x = prop.vector3Value.x, y = prop.vector3Value.y, z = prop.vector3Value.z };
                case SerializedPropertyType.Vector4:
                    return new { x = prop.vector4Value.x, y = prop.vector4Value.y, z = prop.vector4Value.z, w = prop.vector4Value.w };
                case SerializedPropertyType.Rect:
                    return new { x = prop.rectValue.x, y = prop.rectValue.y, width = prop.rectValue.width, height = prop.rectValue.height };
                case SerializedPropertyType.ArraySize:
                    return prop.intValue;
                case SerializedPropertyType.Bounds:
                    return new
                    {
                        center = new { x = prop.boundsValue.center.x, y = prop.boundsValue.center.y, z = prop.boundsValue.center.z },
                        size = new { x = prop.boundsValue.size.x, y = prop.boundsValue.size.y, z = prop.boundsValue.size.z }
                    };
                case SerializedPropertyType.Quaternion:
                    return new { x = prop.quaternionValue.x, y = prop.quaternionValue.y, z = prop.quaternionValue.z, w = prop.quaternionValue.w };
                default:
                    return prop.propertyType.ToString();
            }
        }

        private bool SetPropertyValue(SerializedProperty prop, object value)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Float:
                        if (!TryGetFloat(value, out var floatValue)) return false;
                        prop.floatValue = floatValue;
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value != null ? value.ToString() : string.Empty;
                        return true;
                    case SerializedPropertyType.Enum:
                        return SetEnumValue(prop, value);
                    case SerializedPropertyType.ObjectReference:
                        prop.objectReferenceValue = ResolveObjectReference(value, prop);
                        return true;
                    case SerializedPropertyType.Vector2:
                        if (!TryGetVector2(value, out var vector2Value)) return false;
                        prop.vector2Value = vector2Value;
                        return true;
                    case SerializedPropertyType.Vector3:
                        if (!TryGetVector3(value, out var vector3Value)) return false;
                        prop.vector3Value = vector3Value;
                        return true;
                    case SerializedPropertyType.Vector4:
                        if (!TryGetVector4(value, out var vector4Value)) return false;
                        prop.vector4Value = vector4Value;
                        return true;
                    case SerializedPropertyType.Color:
                        if (!TryGetColor(value, out var colorValue)) return false;
                        prop.colorValue = colorValue;
                        return true;
                    case SerializedPropertyType.Rect:
                        if (!TryGetRect(value, out var rectValue)) return false;
                        prop.rectValue = rectValue;
                        return true;
                    case SerializedPropertyType.ArraySize:
                        prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Bounds:
                        if (!TryGetBounds(value, out var boundsValue)) return false;
                        prop.boundsValue = boundsValue;
                        return true;
                    case SerializedPropertyType.Quaternion:
                        if (!TryGetQuaternion(value, out var quaternionValue)) return false;
                        prop.quaternionValue = quaternionValue;
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool SetEnumValue(SerializedProperty prop, object value)
        {
            if (TryGetInt(value, out var enumIndex))
            {
                if (enumIndex < 0 || enumIndex >= prop.enumNames.Length)
                {
                    return false;
                }

                prop.enumValueIndex = enumIndex;
                return true;
            }

            var enumName = value != null ? value.ToString() : null;
            for (var i = 0; i < prop.enumNames.Length; i++)
            {
                if (prop.enumNames[i] == enumName)
                {
                    prop.enumValueIndex = i;
                    return true;
                }
            }

            return false;
        }

        private UnityEngine.Object ResolveObjectReference(object value, SerializedProperty prop = null)
        {
            if (value == null)
            {
                return null;
            }

            if (value is double doubleId)
            {
                var id = (int)doubleId;
                return id != 0 ? GetObjectByInstanceId(id) : null;
            }

            if (value is long longId)
            {
                var id = (int)longId;
                return id != 0 ? GetObjectByInstanceId(id) : null;
            }

            if (value is int intId)
            {
                return intId != 0 ? GetObjectByInstanceId(intId) : null;
            }

            var str = value.ToString();
            if (string.IsNullOrEmpty(str) || string.Equals(str, "null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var assetPath = str;
            if (!str.StartsWith(AssetsPathPrefix, StringComparison.OrdinalIgnoreCase)
                && !str.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(str);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    assetPath = guidPath;
                }
            }

            if (prop != null)
            {
                var expectedType = GetExpectedTypeFromProperty(prop);
                if (expectedType != null)
                {
                    var typed = AssetDatabase.LoadAssetAtPath(assetPath, expectedType);
                    if (typed != null)
                    {
                        return typed;
                    }
                }

                var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (allAssets != null && allAssets.Length > 1)
                {
                    var propType = prop.type;
                    foreach (var sub in allAssets)
                    {
                        if (sub == null) continue;
                        if (propType.Contains(sub.GetType().Name))
                        {
                            return sub;
                        }
                    }
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null)
            {
                return asset;
            }

            var go = GameObject.Find(str);
            if (go != null)
            {
                return go;
            }

            return null;
        }

        private static bool TryGetInt(object value, out int result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }

            try
            {
                if (value is int intValue)
                {
                    result = intValue;
                    return true;
                }

                if (value is long longValue)
                {
                    result = (int)longValue;
                    return true;
                }

                if (value is double doubleValue)
                {
                    result = (int)doubleValue;
                    return true;
                }

                if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetFloat(object value, out float result)
        {
            result = 0f;
            if (value == null)
            {
                return false;
            }

            try
            {
                if (value is float floatValue)
                {
                    result = floatValue;
                    return true;
                }

                if (value is double doubleValue)
                {
                    result = (float)doubleValue;
                    return true;
                }

                if (value is long longValue)
                {
                    result = longValue;
                    return true;
                }

                if (float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetVector2(object value, out Vector2 result)
        {
            result = Vector2.zero;
            if (!TryReadFloatList(value, 2, out var values))
            {
                return false;
            }

            result = new Vector2(values[0], values[1]);
            return true;
        }

        private static bool TryGetVector3(object value, out Vector3 result)
        {
            result = Vector3.zero;
            if (!TryReadFloatList(value, 3, out var values))
            {
                return false;
            }

            result = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        private static bool TryGetVector4(object value, out Vector4 result)
        {
            result = Vector4.zero;
            if (!TryReadFloatList(value, 4, out var values))
            {
                return false;
            }

            result = new Vector4(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetColor(object value, out Color result)
        {
            result = Color.white;
            if (!TryReadFloatList(value, 4, out var values))
            {
                return false;
            }

            result = new Color(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetRect(object value, out Rect result)
        {
            result = Rect.zero;
            if (!TryReadFloatList(value, 4, out var values))
            {
                return false;
            }

            result = new Rect(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetQuaternion(object value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (!TryReadFloatList(value, 4, out var values))
            {
                return false;
            }

            result = new Quaternion(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetBounds(object value, out Bounds result)
        {
            result = new Bounds();
            if (value is IDictionary dictionary)
            {
                var centerValue = GetDictionaryValue(dictionary, "center");
                var sizeValue = GetDictionaryValue(dictionary, "size");
                if (TryGetVector3(centerValue, out var center) && TryGetVector3(sizeValue, out var size))
                {
                    result = new Bounds(center, size);
                    return true;
                }
            }

            if (TryReadFloatList(value, 6, out var values))
            {
                result = new Bounds(
                    new Vector3(values[0], values[1], values[2]),
                    new Vector3(values[3], values[4], values[5]));
                return true;
            }

            return false;
        }

        private static bool TryReadFloatList(object value, int expectedCount, out float[] values)
        {
            values = null;
            var collected = new List<float>();

            if (value is IDictionary dictionary)
            {
                var keySets = GetFloatKeySets(expectedCount);
                for (var setIndex = 0; setIndex < keySets.Length; setIndex++)
                {
                    collected.Clear();
                    var keys = keySets[setIndex];
                    var success = true;

                    foreach (var key in keys)
                    {
                        if (!TryGetFloat(GetDictionaryValue(dictionary, key), out var number))
                        {
                            success = false;
                            break;
                        }

                        collected.Add(number);
                    }

                    if (success)
                    {
                        values = collected.ToArray();
                        return true;
                    }
                }
            }

            if (value is IList list)
            {
                if (list.Count != expectedCount)
                {
                    return false;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (!TryGetFloat(list[i], out var number))
                    {
                        return false;
                    }

                    collected.Add(number);
                }

                values = collected.ToArray();
                return true;
            }

            var text = value != null ? value.ToString() : null;
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Trim().Trim('(', ')', '[', ']');
                var parts = text.Split(',');
                if (parts.Length != expectedCount)
                {
                    return false;
                }

                for (var i = 0; i < parts.Length; i++)
                {
                    if (!TryGetFloat(parts[i].Trim(), out var number))
                    {
                        return false;
                    }

                    collected.Add(number);
                }

                values = collected.ToArray();
                return true;
            }

            return false;
        }

        private static string[][] GetFloatKeySets(int expectedCount)
        {
            if (expectedCount == 2)
            {
                return new[] { new[] { "x", "y" } };
            }

            if (expectedCount == 3)
            {
                return new[] { new[] { "x", "y", "z" } };
            }

            if (expectedCount == 4)
            {
                return new[]
                {
                    new[] { "x", "y", "z", "w" },
                    new[] { "r", "g", "b", "a" },
                    new[] { "x", "y", "width", "height" }
                };
            }

            return new string[0][];
        }

        private static object GetDictionaryValue(IDictionary dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key != null && string.Equals(entry.Key.ToString(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Extract expected type from SerializedProperty.type (e.g. "PPtr<$Sprite>" -> Sprite).
        /// </summary>
        private static Type GetExpectedTypeFromProperty(SerializedProperty prop)
        {
            var typeName = prop.type;
            var start = typeName.IndexOf('<');
            var end = typeName.IndexOf('>');
            if (start < 0 || end <= start)
            {
                return null;
            }

            var inner = typeName.Substring(start + 1, end - start - 1);
            if (inner.StartsWith("$"))
            {
                inner = inner.Substring(1);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType($"UnityEngine.{inner}");
                if (type != null)
                {
                    return type;
                }

                type = assembly.GetType($"UnityEngine.UI.{inner}");
                if (type != null)
                {
                    return type;
                }

                type = assembly.GetType(inner);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private sealed class TargetContext : IDisposable
        {
            public string AssetPath;
            public string ObjectPath;
            public GameObject PrefabRoot;
            public GameObject GameObject;
            public UnityEngine.Object SerializedTarget;
            public bool IsPrefabAsset;
            public bool IsAssetObject;
            public bool IsSceneObject;

            public void Dispose()
            {
                if (IsPrefabAsset && PrefabRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(PrefabRoot);
                    PrefabRoot = null;
                }
            }
        }

        [Serializable]
        private class ComponentInfo
        {
            public int index;
            public string typeName;
            public string fullTypeName;
            public int instanceId;
            public bool enabled = true;
        }

        [Serializable]
        private class PropertyInfo
        {
            public string name;
            public string propertyPath;
            public string displayName;
            public string propertyType;
            public object value;
            public bool editable;
            public bool isExpanded;
            public bool hasChildren;
            public int depth;
        }

        [Serializable]
        private class PropertyChangeInfo
        {
            public string propertyName;
            public string propertyType;
            public object oldValue;
            public object newValue;
        }
    }
}
