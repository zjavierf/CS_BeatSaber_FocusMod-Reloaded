using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FocusMod.Configuration;
using UnityEngine;
using Zenject;

namespace FocusMod {
	class FocusMod : IInitializable, ITickable {
		const int HiddenHudLayer = 23;
		const int NormalHudLayer = 5;

		GameObject[]? elementsToHide;
		readonly Dictionary<GameObject, CanvasGroup> canvasGroups = new Dictionary<GameObject, CanvasGroup>();
		Coroutine? activeFadeCoroutine;

		readonly AudioTimeSyncController audioTimeSyncController;
		readonly GameplayCoreSceneSetupData gameplayCoreSceneSetupData;
		readonly IReadonlyBeatmapData beatmapData;

		public FocusMod(AudioTimeSyncController audioTimeSyncController, GameplayCoreSceneSetupData gameplayCoreSceneSetupData) {
			this.audioTimeSyncController = audioTimeSyncController;
			this.gameplayCoreSceneSetupData = gameplayCoreSceneSetupData;
			this.beatmapData = gameplayCoreSceneSetupData.transformedBeatmapData;
		}

		static MethodBase? ScoreSaber_playbackEnabled =
			IPA.Loader.PluginManager.GetPluginFromId("ScoreSaber")?
			.Assembly.GetType("ScoreSaber.Core.ReplaySystem.HarmonyPatches.PatchHandleHMDUnmounted")?
			.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);

		struct SafeTimespan {
			public float start;
			public float end;

			public SafeTimespan(float start, float end) {
				this.start = start;
				this.end = end;
			}
		}

		int lastTimespanIndex = 0;
		SafeTimespan[] visibleTimespans = Array.Empty<SafeTimespan>();

		void getElements() {
			var _scoreElement =
				UnityEngine.Object.FindObjectOfType<ImmediateRankUIPanel>()?.gameObject ??
				UnityEngine.Object.FindObjectOfType<ScoreUIController>()?.gameObject;

			if (_scoreElement == null) {
				Plugin.Log?.Warn("[FocusMod] Score element not found!");
				return;
			}

			if (!(PluginConfig.Instance is { HideAll: true })) {
				if (!_scoreElement.GetComponent<Canvas>())
					_scoreElement.AddComponent<Canvas>();

				elementsToHide = new GameObject[] { _scoreElement };
			} else {
				var standardHuds = new GameObject?[] {
					_scoreElement,
					UnityEngine.Object.FindObjectOfType<ScoreMultiplierUIController>()?.gameObject
				};

				var allObjects = UnityEngine.Object.FindObjectsOfType<Transform>()
					.Where(t => t != null && t.gameObject != null)
					.Select(t => t.gameObject)
					.Where(go => {
						string name = go.name.ToLower();
						return name.Contains("counter") || 
						       name.Contains("hud") || 
						       name.Contains("score") || 
						       name.Contains("naluluna") ||
						       name.Contains("progress") ||
						       name.Contains("leftpanel") ||
						       name.Contains("rightpanel");
					})
					.ToArray();

				elementsToHide = standardHuds
					.Concat(allObjects)
					.Concat(
						UnityEngine.Object.FindObjectOfType<ComboUIController>()?.GetComponentsInChildren<Canvas>()?.Select(x => x?.gameObject) ?? Enumerable.Empty<GameObject>()
					)
					.Where(x => x != null)
					.Distinct()
					.ToArray()!;
			}

			elementsToHide = elementsToHide.Where(x => x != null && x.activeSelf).ToArray()!;
			
			canvasGroups.Clear();
			foreach (var elem in elementsToHide) {
				if (elem != null) {
					var cg = elem.GetComponent<CanvasGroup>();
					if (cg == null) {
						cg = elem.AddComponent<CanvasGroup>();
					}
					canvasGroups[elem] = cg;
				}
			}
		}

		void ParseMap(IReadonlyBeatmapData beatmapData) {
			float lastObjectTime = 0f;
			var visibleTimespans = new List<SafeTimespan>();

			void CheckAndAdd(float objectTime, bool isLast = false) {
				float leadTime = PluginConfig.Instance?.LeadTime ?? 1.5f;
				float minDisplay = PluginConfig.Instance?.MinimumDisplaytime ?? 0.5f;

				if (isLast || (objectTime - lastObjectTime - leadTime >= minDisplay))
					visibleTimespans.Add(new SafeTimespan(
						lastObjectTime,
						isLast ? objectTime : objectTime - leadTime
					));

				lastObjectTime = objectTime;
			}

			foreach (var beatmapObject in beatmapData.allBeatmapDataItems) {
				if (beatmapObject.type != BeatmapDataItem.BeatmapDataItemType.BeatmapObject)
					continue;

				if (lastObjectTime == beatmapObject.time)
					continue;

				if (beatmapObject is ObstacleData obs) {
					if (PluginConfig.Instance?.IgnoreWalls == true)
						continue;

					if (obs.width == 1 && (obs.lineIndex == 0 || obs.lineIndex == 3))
						continue;
				} else if (beatmapObject is SliderData sld) {
					if (sld.sliderType == SliderData.Type.Normal)
						continue;

					CheckAndAdd(Math.Max(sld.tailTime, sld.time));
				} else {
					if ((PluginConfig.Instance?.IgnoreBombs == true) && (beatmapObject as NoteData)?.gameplayType == NoteData.GameplayType.Bomb)
						continue;

					CheckAndAdd(beatmapObject.time);
				}
			}

			CheckAndAdd(audioTimeSyncController.songLength, true);
			this.visibleTimespans = visibleTimespans.ToArray();
		}

		public void Initialize() {
			if (PluginConfig.Instance?.LeadTime == 0f)
				return;

			try {
				if (ScoreSaber_playbackEnabled != null && !(bool)ScoreSaber_playbackEnabled.Invoke(null, null))
					return;
			} catch {
				// Ignored
			}

			var njs = gameplayCoreSceneSetupData.beatmapBasicData.noteJumpMovementSpeed;
			if (njs < (PluginConfig.Instance?.MinimumNjs ?? 14))
				return;

			ParseMap(beatmapData);
			CoroutineRunner.Start(InitStuff());
		}

		IEnumerator InitStuff() {
			yield return null;

			getElements();
			if (elementsToHide == null || elementsToHide.Length == 0)
				yield break;

			int HudToggle(int flag, bool show = true) => show ? flag | 1 << HiddenHudLayer : flag & ~(1 << HiddenHudLayer);

			foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>()) {
				bool hideOnlyInHmd = PluginConfig.Instance?.HideOnlyInHMD ?? true;
				if (!hideOnlyInHmd || cam.name == "MainCamera") {
					cam.cullingMask = HudToggle(cam.cullingMask, false);
				} else {
					cam.cullingMask = HudToggle(cam.cullingMask, (cam.cullingMask & (1 << NormalHudLayer)) != 0);
				}
			}

			var LIVTHING = UnityEngine.Object.FindObjectOfType<LIV.SDK.Unity.LIV>();
			if (LIVTHING != null)
				LIVTHING.spectatorLayerMask = HudToggle(LIVTHING.spectatorLayerMask, PluginConfig.Instance?.HideOnlyInHMD ?? true);
		}

		bool isVisible = true;

		private void SetHudVisibility(bool visible) {
			if (isVisible == visible || elementsToHide == null)
				return;

			isVisible = visible;

			if (activeFadeCoroutine != null) {
				CoroutineRunner.Stop(activeFadeCoroutine);
			}
			activeFadeCoroutine = CoroutineRunner.Start(FadeRoutine(visible, 0.5f));
		}

		IEnumerator FadeRoutine(bool targetVisible, float duration) {
			if (targetVisible) {
				foreach (var elem in elementsToHide!) {
					if (elem != null) elem.SetActive(true);
				}
			}

			float elapsedTime = 0f;
			Dictionary<GameObject, float> startAlphas = new Dictionary<GameObject, float>();
			foreach (var kvp in canvasGroups) {
				if (kvp.Key != null) {
					startAlphas[kvp.Key] = kvp.Value.alpha;
				}
			}

			while (elapsedTime < duration) {
				elapsedTime += Time.deltaTime;
				float t = Mathf.Clamp01(elapsedTime / duration);

				foreach (var kvp in canvasGroups) {
					if (kvp.Key != null && startAlphas.ContainsKey(kvp.Key)) {
						float startAlpha = startAlphas[kvp.Key];
						float targetAlpha = targetVisible ? 1f : 0f;
						kvp.Value.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
					}
				}
				yield return null;
			}

			foreach (var kvp in canvasGroups) {
				if (kvp.Key != null) {
					kvp.Value.alpha = targetVisible ? 1f : 0f;
				}
			}

			if (!targetVisible) {
				foreach (var elem in elementsToHide!) {
					if (elem != null) elem.SetActive(false);
				}
			}
		}

		public void Tick() {
			if (elementsToHide == null || visibleTimespans.Length == 0 || audioTimeSyncController == null)
				return;

			try {
				var isPaused = audioTimeSyncController.state != AudioTimeSyncController.State.Playing;

				if (isPaused && isVisible)
					return;

				if (lastTimespanIndex >= visibleTimespans.Length)
					return;

				if (lastTimespanIndex != 0 && audioTimeSyncController.songTime < visibleTimespans[lastTimespanIndex - 1].end)
					lastTimespanIndex = 0;

				var intendedVisibility = false;

				for (var i = lastTimespanIndex; i < visibleTimespans.Length; i++) {
					var ts = visibleTimespans[i];

					if (ts.start > audioTimeSyncController.songTime)
						break;

					if (ts.end < audioTimeSyncController.songTime) {
						lastTimespanIndex++;
						continue;
					}

					if (ts.start < audioTimeSyncController.songTime) {
						intendedVisibility = true;
						break;
					}
				}

				SetHudVisibility(intendedVisibility || (isPaused && (PluginConfig.Instance?.UnhideInPause ?? false)));
			} catch {
				// Suppress transition errors
			}
		}
	}

	internal class CoroutineRunner : MonoBehaviour {
		private static CoroutineRunner? _instance;
		public static Coroutine Start(IEnumerator routine) {
			if (_instance == null) {
				var obj = new GameObject("FocusMod_CoroutineRunner");
				_instance = obj.AddComponent<CoroutineRunner>();
				DontDestroyOnLoad(obj);
			}
			return _instance.StartCoroutine(routine);
		}

		public static void Stop(Coroutine routine) {
			if (_instance != null && routine != null) {
				_instance.StopCoroutine(routine);
			}
		}
	}
}
