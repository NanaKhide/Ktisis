using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Ktisis.Common.Utility;
using Ktisis.Core.Attributes;
using Ktisis.Data.Files;
using Ktisis.Editor.Context;
using Ktisis.Scene.Modules.Actors;
using Newtonsoft.Json;

using Ktisis.Data.Config;
using Ktisis.Editor.Context;
using Ktisis.Editor.Context.Types;
using Ktisis.Interface.Components.Config;
using Ktisis.Interface.Components.Transforms;
using Ktisis.Interface.Types;
using Ktisis.Services.Data;
using Ktisis.Localization;
using Ktisis.Interop.Ipc;
using Ktisis.Interface.Overlay;
using Ktisis.Scene.Entities.Skeleton;
using Ktisis.Common.Utility;

namespace Ktisis.Interface.Windows;

public class DebugWindow : KtisisWindow {
	private readonly IEditorContext _ctx;
	private readonly GuiManager _gui;
	private readonly TransformTable _transformTable;

	// tester inputs
	private int _gameObjectId;
	private bool _hasClip = false;

	// tester outputs
	private (int, int)? _apiVersion = null;
	private bool? _isPosing = null;

	//Transform stuff
	private Transform _transform = new();
	private string _boneName = "j_kao";
	private bool _useWorldSpace = false;
	private string _batchBoneNames = "j_kao";

	// ktisis subscriptions
	private readonly ICallGateSubscriber<(int, int)> _ktisisApiVersion;
	private readonly ICallGateSubscriber<bool> _ktisisRefreshActors;
	private readonly ICallGateSubscriber<bool> _ktisisIsPosing;
	private readonly ICallGateSubscriber<uint, string, Task<bool>> _ktisisLoadPose;
	private readonly ICallGateSubscriber<uint, Task<string?>> _ktisisSavePose;
	private readonly ICallGateSubscriber<Task<Dictionary<int, HashSet<string>>>> _ktisisSelectedBones;


	private readonly ICallGateSubscriber<uint, string, bool, Transform?> _ktisisGetTransform;
	private readonly ICallGateSubscriber<uint, string, Transform, bool, bool> _ktisisSetTransform;
	private readonly ICallGateSubscriber<uint, List<string>, bool, Dictionary<string, Transform?>> _ktisisBatchGetTransform;
	private readonly ICallGateSubscriber<uint, bool, Dictionary<string, Transform?>> _ktisisGetAllTransforms;
	private readonly ICallGateSubscriber<uint, Dictionary<string, Transform>, bool, bool> _ktisisBatchSetTransform;
	public DebugWindow(
		IEditorContext ctx,
		GuiManager gui,
		IDalamudPluginInterface dpi,
		ConfigManager cfg,
		LocaleManager locale
	) : base(
		"Debug Window"
	) {
		this._ctx = ctx;
		this._gui = gui;

		// create our IPC subs from DPI
		this._ktisisApiVersion = dpi.GetIpcSubscriber<(int, int)>("Ktisis.ApiVersion");
		this._ktisisRefreshActors = dpi.GetIpcSubscriber<bool>("Ktisis.RefreshActors");
		this._ktisisIsPosing = dpi.GetIpcSubscriber<bool>("Ktisis.IsPosing");
		this._ktisisLoadPose = dpi.GetIpcSubscriber<uint, string, Task<bool>>("Ktisis.LoadPose");
		this._ktisisSavePose = dpi.GetIpcSubscriber<uint, Task<string?>>("Ktisis.SavePose");
		this._ktisisSelectedBones = dpi.GetIpcSubscriber<Task<Dictionary<int, HashSet<string>>>>("Ktisis.SelectedBones");

		// Transform subs
		this._ktisisGetTransform = dpi.GetIpcSubscriber<uint, string, bool, Transform?>("Ktisis.GetTransform");
		this._ktisisSetTransform = dpi.GetIpcSubscriber<uint, string, Transform, bool, bool>("Ktisis.SetTransform");
		this._ktisisBatchGetTransform = dpi.GetIpcSubscriber<uint, List<string>, bool, Dictionary<string, Transform?>>("Ktisis.BatchGetTransform");
		this._ktisisGetAllTransforms = dpi.GetIpcSubscriber<uint, bool, Dictionary<string, Transform?>>("Ktisis.GetAllTransforms");
		this._ktisisBatchSetTransform = dpi.GetIpcSubscriber<uint, Dictionary<string, Transform>, bool, bool>("Ktisis.BatchSetTransform");

		this._transformTable = new TransformTable(cfg, locale);
	}

	public override void Draw() {
		if (!this._ctx.IsValid) {
			this.Close();
			return;
		}

		using var tabs = ImRaii.TabBar("##ConfigTabs");
		if (!tabs.Success) return;
		DrawTab("IPC Provider", this.DrawProviderTab);
		DrawTab("IPC Manager", this.DrawManagerTab);
		DrawTab("Diagnostics", this.DrawDiagnosticsTab);
	}
	private static void DrawTab(string name, Action handler) {
		using var tab = ImRaii.TabItem(name);
		if (!tab.Success) return;
		ImGui.Spacing();
		handler.Invoke();
	}

	private async void DrawProviderTab() {
		ImGui.InputInt("GameObject Index", ref _gameObjectId);
		ImGui.Text($"Clipboard Pose Data: {_hasClip}");
		ImGui.Spacing();

		ImGui.Text("Ktisis.ApiVersion");
		if (ImGui.Button("GET##ApiVersion"))
			_apiVersion = this._ktisisApiVersion.InvokeFunc();
		ImGui.SameLine();
		using (ImRaii.Disabled(_apiVersion == null))
			ImGui.Text($"Version: {_apiVersion}");
		ImGui.Spacing();

		ImGui.Text("Ktisis.RefreshActors");
		if (ImGui.Button("APPLY##RefreshActors"))
			this._ktisisRefreshActors.InvokeFunc();
		ImGui.Spacing();

		ImGui.Text("Ktisis.IsPosing");
		if (ImGui.Button("GET##IsPosing"))
			_isPosing = this._ktisisIsPosing.InvokeFunc();
		ImGui.SameLine();
		using (ImRaii.Disabled(_isPosing == null))
			ImGui.Text($"Posing: {_isPosing}");
		ImGui.Spacing();

		ImGui.Text("Ktisis.LoadPose");
		using (ImRaii.Disabled(_gameObjectId < 1 || !_hasClip))
			if (ImGui.Button("APPLY (Clipboard)##LoadPose"))
			{
				_hasClip = CheckClipboard();
				if (_hasClip)
				{
					var applied = await this._ktisisLoadPose.InvokeFunc((uint)_gameObjectId, ImGui.GetClipboardText());
					if (applied)
						Ktisis.Log.Debug($"[DEBUG] Loaded clipboard pose to actor {_gameObjectId}");
					else
						Ktisis.Log.Warning($"[DEBUG] Failed clipboard pose application to actor {_gameObjectId}");
				}
				else
					Ktisis.Log.Warning("[DEBUG] Clipboard has invalid pose data, cannot apply");
			}
		ImGui.Spacing();

		// todo: popup bubble with the json output ala glamourer IPC tester
		ImGui.Text("Ktisis.SavePose");
		using (ImRaii.Disabled(_gameObjectId < 1))
			if (ImGui.Button("GET (Clipboard)##SavePose"))
			{
				var clip = await this._ktisisSavePose.InvokeFunc((uint)_gameObjectId);
				ImGui.SetClipboardText(clip);
				_hasClip = true;
				Ktisis.Log.Debug($"[DEBUG] Exported pose to clipboard from actor {_gameObjectId}: {clip}");
			}
		ImGui.Spacing();

		ImGui.Text("Ktisis.SelectedBones");
		if (ImGui.Button("GET##SelectedBones"))
		{
			var bones = await this._ktisisSelectedBones.InvokeFunc();

			foreach (var (actorId, boneSet) in bones)
			{
				Ktisis.Log.Debug($"[DEBUG] Actor {actorId} selected bones: {string.Join(", ", boneSet)}");
			}
		}

		ImGui.Separator();
		ImGui.Text("Transform IPC");
		ImGui.InputText("Bone Name", ref _boneName, 64);
		ImGui.Checkbox("Use World Space", ref _useWorldSpace);

		var pos = _transform.Position;
		if (ImGui.DragFloat3("Position", ref pos, 0.01f)) _transform.Position = pos;

		var rot = _transform.Rotation;
		var vRot = new Vector4(rot.X, rot.Y, rot.Z, rot.W);
		if (ImGui.DragFloat4("Rotation (Quat)", ref vRot, 0.01f)) _transform.Rotation = new Quaternion(vRot.X, vRot.Y, vRot.Z, vRot.W);

		var scale = _transform.Scale;
		if (ImGui.DragFloat3("Scale", ref scale, 0.01f)) _transform.Scale = scale;

		if (ImGui.Button("Get##Transform"))
		{
			var res = _ktisisGetTransform.InvokeFunc((uint)_gameObjectId, _boneName, _useWorldSpace);
			if (res != null)
			{
				_transform = res;
				Ktisis.Log.Debug($"[DEBUG] GetTransform {_boneName}: Pos:{res.Position} Rot:{res.Rotation} Scale:{res.Scale}");
			} else
			{
				Ktisis.Log.Warning($"[DEBUG] GetTransform {_boneName} returned null");
			}
		}
		ImGui.SameLine();
		if (ImGui.Button("Set##Transform"))
		{
			var success = _ktisisSetTransform.InvokeFunc((uint)_gameObjectId, _boneName, _transform, _useWorldSpace);
			Ktisis.Log.Debug($"[DEBUG] SetTransform {_boneName} success: {success}");
		}

		ImGui.Spacing();
		ImGui.InputText("Batch Bones (CSV)", ref _batchBoneNames, 256);
		if (ImGui.Button("Batch Get"))
		{
			var names = _batchBoneNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
			var res = _ktisisBatchGetTransform.InvokeFunc((uint)_gameObjectId, names, _useWorldSpace);
			if (res != null)
			{
				Ktisis.Log.Debug($"[DEBUG] BatchGet returned {res.Count} items.");
				foreach (var (k, v) in res)
				{
					if (v != null) Ktisis.Log.Debug($" -> {k}: {v.Position}");
				}
			}
		}
		ImGui.SameLine();
		if (ImGui.Button("Batch Set (All to Current)"))
		{
			var names = _batchBoneNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			var dict = new Dictionary<string, Transform>();
			foreach (var name in names) dict[name] = _transform;

			var success = _ktisisBatchSetTransform.InvokeFunc((uint)_gameObjectId, dict, _useWorldSpace);
			Ktisis.Log.Debug($"[DEBUG] BatchSet success: {success}");
		}

		if (ImGui.Button("Get All Transforms"))
		{
			var res = _ktisisGetAllTransforms.InvokeFunc((uint)_gameObjectId, _useWorldSpace);
			Ktisis.Log.Debug($"[DEBUG] GetAllTransforms returned {res?.Count ?? 0} bones.");
		}
	}

	private void DrawManagerTab() {
		ImGui.Text("TODO");
	}

	private void DrawDiagnosticsTab() {
		// existing debug text from overlay
		var overlay = this._gui.Get<OverlayWindow>();
		overlay.DrawDebug(null);

		// todo: scenetree / actors and entities details
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();
		var target = this._ctx.Transform.Target;
		if (target?.GetTransform() == null)
			return;
		var trans = target.GetTransform()!;
		ImGui.Text($"Target: {target.Primary?.Name}");
		ImGui.Text($"Position:\n\tX: {trans.Position.X}\n\tY: {trans.Position.Y}\n\tZ: {trans.Position.Z}");
		ImGui.Text($"Rotation:\n\tX: {trans.Rotation.X}\n\tY: {trans.Rotation.Y}\n\tZ: {trans.Rotation.Z}\n\tW: {trans.Rotation.W}");
		ImGui.Text($"Scale:\n\tX: {trans.Scale.X}\n\tY: {trans.Scale.Y}\n\tZ: {trans.Scale.Z}");
		var selection = this._ctx.Selection.GetFirstSelected();
		if (selection is BoneNode bone) {
			var matrix = bone.GetMatrixModel()!?? Matrix4x4.Identity;
			Matrix4x4.Decompose(
				matrix,
				out var scl,
				out var rot,
				out var pos
			);
			var t = bone.GetTransformModel() ?? new Transform();
			ImGui.Spacing();
			ImGui.Text($"Havok (Matrix Decompose / Raw Transform)");
			ImGui.Text($"Position:\n\tX: {pos.X} / {t.Position.X}\n\tY: {pos.Y} / {t.Position.Y}\n\tZ: {pos.Z} / {t.Position.Z}");
			ImGui.Text($"Rotation:\n\tX: {rot.X} / {t.Rotation.X}\n\tY: {rot.Y} / {t.Rotation.Y}\n\tZ: {rot.Z} / {t.Rotation.Z}\n\tW: {rot.W} / {t.Rotation.W}");
			ImGui.Text($"Scale:\n\tX: {scl.X} / {t.Scale.X}\n\tY: {scl.Y} / {t.Scale.Y}\n\tZ: {scl.Z} / {t.Scale.Z}");
		}
	}

	private bool CheckClipboard() {
		var text = ImGui.GetClipboardText();
		if (text != null) {
			try {
				var file = JsonConvert.DeserializeObject<PoseFile>(text);
				if (file != null)
					return true;
			} catch {
				return false;
			}
		}
		return false;
	}
}
