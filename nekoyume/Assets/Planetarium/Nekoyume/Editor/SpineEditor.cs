using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Nekoyume.Game.Character;
using Spine.Unity;
using Spine.Unity.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Planetarium.Nekoyume.Editor
{
    // TODO: Costume, NPC 로직 추가
    // TODO: 사용자가 알기 쉽게 예외 상황 전부 알림 띄워주기.
    public static class SpineEditor
    {
        private const string FindAssetFilter = "CharacterAnimator t:AnimatorController";

        private const string FullCostumePrefabPath = "Assets/Resources/Character/FullCostume";

        private const string FullCostumeSpineRootPath =
            "Assets/AddressableAssets/Character/FullCostume";

        private const string MonsterPrefabPath = "Assets/Resources/Character/Monster";
        private const string MonsterSpineRootPath = "Assets/AddressableAssets/Character/Monster";

        private const string NPCPrefabPath = "Assets/Resources/Character/NPC";
        private const string NPCSpineRootPath = "Assets/AddressableAssets/Character/NPC";

        private const string PlayerPrefabPath = "Assets/Resources/Character/Player";
        private const string PlayerSpineRootPath = "Assets/AddressableAssets/Character/Player";

        private static readonly Vector3 Position = Vector3.zero;

        /// <summary>
        /// 헤어 스타일을 결정하는 정보를 스파인이 포함하지 않기 때문에 이곳에 하드코딩해서 구분해 준다.
        /// </summary>
        private static readonly string[] HairType1Names =
        {
            "10230000", "10231000", "10232000", "10233000", "10234000", "10235000"
        };

        [MenuItem("Assets/9C/Create Spine Prefab", true)]
        public static bool CreateSpinePrefabValidation()
        {
            return Selection.activeObject is SkeletonDataAsset;
        }

        [MenuItem("Assets/9C/Create Spine Prefab", false, 0)]
        public static void CreateSpinePrefab()
        {
            if (!(Selection.activeObject is SkeletonDataAsset skeletonDataAsset))
            {
                return;
            }

            CreateSpinePrefabInternal(skeletonDataAsset);
        }

        [MenuItem("Tools/9C/Create Spine Prefab(All FullCostume)", false, 0)]
        public static void CreateSpinePrefabAllOfFullCostume()
        {
            CreateSpinePrefabAllOfPath(FullCostumeSpineRootPath);
        }

        [MenuItem("Tools/9C/Create Spine Prefab(All Monster)", false, 0)]
        public static void CreateSpinePrefabAllOfMonster()
        {
            CreateSpinePrefabAllOfPath(MonsterSpineRootPath);
        }

        // FIXME: ArgumentNotFoundException 발생.
        // NPC의 경우에 `Idle_01`과 같이 각 상태 분류의 첫 번째 작명에 `_01`이라는 숫자가 들어가 있기 때문에태
        // `CharacterAnimation.Type`을 같이 사용할 수 없는 상황이다.
        // 따라서 NPC 스파인의 상태 작명을 수정한 후에 사용해야 한다.
        // [MenuItem("Tools/9C/Create Spine Prefab(All NPC)", false, 0)]
        public static void CreateSpinePrefabAllOfNPC()
        {
            CreateSpinePrefabAllOfPath(NPCSpineRootPath);
        }

        [MenuItem("Tools/9C/Create Spine Prefab(All Player)", false, 0)]
        public static void CreateSpinePrefabAllOfPlayer()
        {
            CreateSpinePrefabAllOfPath(PlayerSpineRootPath);
        }

        private static string GetPrefabPath(string prefabName)
        {
            string pathFormat = null;
            if (IsFullCostume(prefabName))
            {
                pathFormat = FullCostumePrefabPath;
            }

            if (IsMonster(prefabName))
            {
                pathFormat = MonsterPrefabPath;
            }

            if (IsNPC(prefabName))
            {
                pathFormat = NPCPrefabPath;
            }

            if (IsPlayer(prefabName))
            {
                pathFormat = PlayerPrefabPath;
            }

            return string.IsNullOrEmpty(pathFormat)
                ? null
                : Path.Combine(pathFormat, $"{prefabName}.prefab");
        }

        private static void CreateSpinePrefabInternal(SkeletonDataAsset skeletonDataAsset)
        {
            var assetPath = AssetDatabase.GetAssetPath(skeletonDataAsset);
            var assetFolderPath = assetPath.Replace(Path.GetFileName(assetPath), "");
            var animationAssetsPath = Path.Combine(assetFolderPath, "ReferenceAssets");
            var split = assetPath.Split('/');
            var prefabName = split[split.Length > 1 ? split.Length - 2 : 0];
            var prefabPath = GetPrefabPath(prefabName);

            if (!ValidateSpineResource(prefabName, skeletonDataAsset))
            {
                return;
            }

            CreateAnimationReferenceAssets(skeletonDataAsset);

            var skeletonAnimation =
                SpineEditorUtilities.EditorInstantiation.InstantiateSkeletonAnimation(
                    skeletonDataAsset);
            skeletonAnimation.AnimationName = nameof(CharacterAnimation.Type.Idle);

            var gameObject = skeletonAnimation.gameObject;
            gameObject.name = prefabName;
            gameObject.layer = LayerMask.NameToLayer("Character");
            gameObject.transform.position = Position;
            gameObject.transform.localScale = GetPrefabLocalScale(prefabName);

            var meshRenderer = gameObject.GetComponent<MeshRenderer>();
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.sortingLayerName = "Character";

            var animatorControllerGuidArray = AssetDatabase.FindAssets(FindAssetFilter);
            if (animatorControllerGuidArray.Length == 0)
            {
                Object.DestroyImmediate(gameObject);
                throw new AssetNotFoundException(
                    $"AssetDatabase.FindAssets(\"{FindAssetFilter}\")");
            }

            var animatorControllerPath =
                AssetDatabase.GUIDToAssetPath(animatorControllerGuidArray[0]);
            var animator = gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController =
                AssetDatabase.LoadAssetAtPath<AnimatorController>(animatorControllerPath);

            var controller = GetOrCreateSpineController(prefabName, gameObject);
            // 지금은 예상 외의 애니메이션을 찾지 못하는 로직이다.
            // animationAssetsPath 하위에 있는 모든 것을 검사..?
            // 애초에 CreateAnimationReferenceAssets() 단계에서 검사할 수 있겠다.
            foreach (var animationType in CharacterAnimation.List)
            {
                assetPath = Path.Combine(animationAssetsPath, $"{animationType}.asset");
                var asset = AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(assetPath);
                if (asset is null)
                {
                    switch (animationType)
                    {
                        // todo: `CharacterAnimation.Type.Appear`와 `CharacterAnimation.Type.Disappear`는 없어질 예정.
                        default:
                            assetPath = Path.Combine(
                                animationAssetsPath,
                                $"{nameof(CharacterAnimation.Type.Idle)}.asset");
                            asset = AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(
                                assetPath);
                            break;
                        case CharacterAnimation.Type.Idle:
                            Object.DestroyImmediate(gameObject);
                            throw new AssetNotFoundException(assetPath);
                        case CharacterAnimation.Type.Win_02:
                        case CharacterAnimation.Type.Win_03:
                            assetPath = Path.Combine(
                                animationAssetsPath,
                                $"{nameof(CharacterAnimation.Type.Win)}.asset");
                            asset = AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(
                                assetPath);
                            break;
                        case CharacterAnimation.Type.Touch:
                        case CharacterAnimation.Type.CastingAttack:
                        case CharacterAnimation.Type.CriticalAttack:
                            assetPath = Path.Combine(
                                animationAssetsPath,
                                $"{nameof(CharacterAnimation.Type.Attack)}.asset");
                            asset = AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(
                                assetPath);
                            break;
                        case CharacterAnimation.Type.TurnOver_01:
                        case CharacterAnimation.Type.TurnOver_02:
                            assetPath = Path.Combine(
                                animationAssetsPath,
                                $"{nameof(CharacterAnimation.Type.Die)}.asset");
                            asset = AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(
                                assetPath);
                            break;
                    }

                    if (asset is null)
                    {
                        assetPath = Path.Combine(
                            animationAssetsPath,
                            $"{nameof(CharacterAnimation.Type.Idle)}.asset");
                        asset = AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(assetPath);
                    }

                    if (asset is null)
                    {
                        Object.DestroyImmediate(gameObject);
                        throw new AssetNotFoundException(assetPath);
                    }
                }

                controller.statesAndAnimations.Add(
                    new SpineController.StateNameToAnimationReference
                    {
                        stateName = animationType.ToString(),
                        animation = asset
                    });
            }

            // 헤어타입을 결정한다.
            if (controller is PlayerSpineController playerSpineController)
            {
                playerSpineController.hairTypeIndex = HairType1Names.Contains(prefabName)
                    ? 1
                    : 0;
            }

            if (File.Exists(prefabPath))
            {
                var boxCollider = controller.GetComponent<BoxCollider>();
                var sourceBoxCollider = AssetDatabase.LoadAssetAtPath<BoxCollider>(prefabPath);
                boxCollider.center = sourceBoxCollider.center;
                boxCollider.size = sourceBoxCollider.size;

                AssetDatabase.DeleteAsset(prefabPath);
            }

            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(gameObject, prefabPath);
                Object.DestroyImmediate(gameObject);
                Selection.activeObject = prefab;
            }
            catch
            {
                Object.DestroyImmediate(gameObject);
                throw new FailedToSaveAsPrefabAssetException(prefabPath);
            }
        }

        #region Character Type

        private static bool IsFullCostume(string prefabName)
        {
            return prefabName.StartsWith("4");
        }

        private static bool IsMonster(string prefabName)
        {
            return prefabName.StartsWith("2");
        }

        private static bool IsNPC(string prefabName)
        {
            return prefabName.StartsWith("3");
        }

        private static bool IsPlayer(string prefabName)
        {
            return prefabName.StartsWith("1");
        }

        #endregion

        #region Validate Spine Resource

        private static bool ValidateSpineResource(
            string prefabName,
            SkeletonDataAsset skeletonDataAsset)
        {
            if (IsFullCostume(prefabName))
            {
                return ValidateForFullCostume(skeletonDataAsset);
            }

            if (IsMonster(prefabName))
            {
                return ValidateForMonster(skeletonDataAsset);
            }

            if (IsNPC(prefabName))
            {
                return ValidateForNPC(skeletonDataAsset);
            }

            if (IsPlayer(prefabName))
            {
                return ValidateForPlayer(skeletonDataAsset);
            }

            return false;
        }

        private static bool ValidateForFullCostume(SkeletonDataAsset skeletonDataAsset)
        {
            var data = skeletonDataAsset.GetSkeletonData(false);
            var hud = data.FindBone("HUD");

            return !(hud is null);
        }

        private static bool ValidateForMonster(SkeletonDataAsset skeletonDataAsset)
        {
            var data = skeletonDataAsset.GetSkeletonData(false);
            var hud = data.FindBone("HUD");

            return !(hud is null);
        }

        private static bool ValidateForNPC(SkeletonDataAsset skeletonDataAsset)
        {
            return true;
        }

        private static bool ValidateForPlayer(SkeletonDataAsset skeletonDataAsset)
        {
            var data = skeletonDataAsset.GetSkeletonData(false);
            var hud = data.FindBone("HUD");

            // TODO: 커스터마이징 슬롯 검사.

            return !(hud is null);
        }

        #endregion

        // CharacterAnimation.Type에서 포함하지 않는 것을 이곳에서 걸러낼 수도 있겠다.
        /// <summary>
        /// `SkeletonDataAssetInspector.CreateAnimationReferenceAssets(): 242`
        /// </summary>
        /// <param name="skeletonDataAsset"></param>
        private static void CreateAnimationReferenceAssets(SkeletonDataAsset skeletonDataAsset)
        {
            const string assetFolderName = "ReferenceAssets";

            var parentFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(skeletonDataAsset));
            var dataPath = parentFolder + "/" + assetFolderName;
            if (AssetDatabase.IsValidFolder(dataPath))
            {
                Directory.Delete(dataPath, true);
            }

            AssetDatabase.CreateFolder(parentFolder, assetFolderName);

            var nameField =
                typeof(AnimationReferenceAsset).GetField(
                    "animationName",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (nameField is null)
            {
                throw new NullReferenceException(
                    "typeof(AnimationReferenceAsset).GetField(\"animationName\", BindingFlags.NonPublic | BindingFlags.Instance);");
            }

            var skeletonDataAssetField = typeof(AnimationReferenceAsset).GetField(
                "skeletonDataAsset",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (skeletonDataAssetField is null)
            {
                throw new NullReferenceException(
                    "typeof(AnimationReferenceAsset).GetField(\"skeletonDataAsset\", BindingFlags.NonPublic | BindingFlags.Instance);");
            }

            var skeletonData = skeletonDataAsset.GetSkeletonData(false);
            foreach (var animation in skeletonData.Animations)
            {
                var assetPath =
                    $"{dataPath}/{SpineEditorUtilities.AssetUtility.GetPathSafeName(animation.Name)}.asset";
                var existingAsset =
                    AssetDatabase.LoadAssetAtPath<AnimationReferenceAsset>(assetPath);
                if (!(existingAsset is null))
                {
                    continue;
                }

                var newAsset = ScriptableObject.CreateInstance<AnimationReferenceAsset>();
                skeletonDataAssetField.SetValue(newAsset, skeletonDataAsset);
                nameField.SetValue(newAsset, animation.Name);
                AssetDatabase.CreateAsset(newAsset, assetPath);
            }

            var folderObject = AssetDatabase.LoadAssetAtPath(dataPath, typeof(Object));
            if (!(folderObject is null))
            {
                Selection.activeObject = folderObject;
                EditorGUIUtility.PingObject(folderObject);
            }
        }

        private static SpineController GetOrCreateSpineController(string prefabName,
            GameObject target)
        {
            if (IsPlayer(prefabName) ||
                IsFullCostume(prefabName))
            {
                return target.AddComponent<PlayerSpineController>();
            }

            if (IsNPC(prefabName))
            {
                return target.AddComponent<NPCSpineController>();
            }

            return target.AddComponent<CharacterSpineController>();
        }

        private static void CreateSpinePrefabAllOfPath(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                Debug.LogWarning($"Not Found Folder! {path}");
                return;
            }

            var subFolderPaths = AssetDatabase.GetSubFolders(path);
            foreach (var subFolderPath in subFolderPaths)
            {
                var id = Path.GetFileName(subFolderPath);
                var skeletonDataAssetPath = Path.Combine(subFolderPath, $"{id}_SkeletonData.asset");
                Debug.Log($"Try to create spine prefab with {skeletonDataAssetPath}");
                var skeletonDataAsset =
                    AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(skeletonDataAssetPath);
                if (ReferenceEquals(skeletonDataAsset, null) || skeletonDataAsset == null)
                {
                    Debug.LogError($"Not Found SkeletonData from {skeletonDataAssetPath}");
                    continue;
                }

                CreateSpinePrefabInternal(skeletonDataAsset);
            }
        }

        // NOTE: 모든 캐릭터는 원본의 해상도를 보여주기 위해서 Vector3.one 사이즈로 스케일되어야 맞습니다.
        // 하지만 이 프로젝트는 2D 리소스의 ppu와 카메라 사이즈가 호환되지 않아서 임의의 스케일을 설정합니다.
        // 이마저도 아트 단에서 예상하지 못한 스케일 이슈가 생기면 "300005"와 같이 예외적인 케이스가 발생합니다.
        // 앞으로 이런 예외가 많아질 것을 대비해서 별도의 함수로 뺍니다.
        private static Vector3 GetPrefabLocalScale(string prefabName)
        {
            switch (prefabName)
            {
                default:
                    return new Vector3(.64f, .64f, 1f);
                case "300005":
                    return new Vector3(.8f, .8f, 1f);
            }
        }
    }
}
