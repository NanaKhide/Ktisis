using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Ktisis.Core.Attributes;
using Ktisis.Data.Files;
using Ktisis.Editor.Context;
using Ktisis.Editor.Context.Types;
using Ktisis.Editor.Posing.Data;
using Ktisis.Editor.Transforms;
using Ktisis.Scene.Entities.Game;
using Ktisis.Scene.Entities.Skeleton;
using Ktisis.Scene.Modules.Actors;
using Ktisis.Common.Utility;
using Newtonsoft.Json;

namespace Ktisis.Interop.Ipc;

[Singleton]
public class IpcProvider(ContextManager ctxManager, IDalamudPluginInterface dpi, IFramework framework) : IDisposable {
	private ICallGateProvider<(int, int)> IpcVersion { get; } = dpi.GetIpcProvider<(int, int)>("Ktisis.ApiVersion");
	private ICallGateProvider<bool> IpcRefreshActions { get; } = dpi.GetIpcProvider<bool>("Ktisis.RefreshActors");
	private ICallGateProvider<bool> IpcIsPosing { get; } = dpi.GetIpcProvider<bool>("Ktisis.IsPosing");
	private ICallGateProvider<uint, string, Task<bool>> IpcLoadPose { get; } = dpi.GetIpcProvider<uint, string, Task<bool>>("Ktisis.LoadPose");
	private ICallGateProvider<uint, string, bool, bool, bool, Task<bool>> IpcLoadPoseExtended { get; } = dpi.GetIpcProvider<uint, string, bool, bool, bool, Task<bool>>("Ktisis.LoadPoseExtended");
	private ICallGateProvider<uint, Task<string?>> IpcSavePose { get; } = dpi.GetIpcProvider<uint, Task<string?>>("Ktisis.SavePose");

	private ICallGateProvider<uint, string, Transform, bool, bool> IpcSetTransform { get; } = dpi.GetIpcProvider<uint, string, Transform, bool, bool>("Ktisis.SetTransform");
	private ICallGateProvider<uint, string, bool, Transform?> IpcGetTransform { get; } = dpi.GetIpcProvider<uint, string, bool, Transform?>("Ktisis.GetTransform");
	private ICallGateProvider<uint, List<string>, bool, Dictionary<string, Transform?>> IpcBatchGetTransform { get; } = dpi.GetIpcProvider<uint, List<string>, bool, Dictionary<string, Transform?>>("Ktisis.BatchGetTransform");
	private ICallGateProvider<uint, Dictionary<string, Transform>, bool, bool> IpcBatchSetTransform { get; } = dpi.GetIpcProvider<uint, Dictionary<string, Transform>, bool, bool>("Ktisis.BatchSetTransform");
	private ICallGateProvider<uint, bool, Dictionary<string, Transform?>> IpcGetAllTransforms { get; } = dpi.GetIpcProvider<uint, bool, Dictionary<string, Transform?>>("Ktisis.GetAllTransforms");

	private ICallGateProvider<Task<Dictionary<int, HashSet<string>>>> IpcSelectedBones { get; } = dpi.GetIpcProvider<Task<Dictionary<int, HashSet<string>>>>("Ktisis.SelectedBones");

	private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

	private T RunOnMainThread<T>(Func<T> func) {
		if (Environment.CurrentManagedThreadId == _mainThreadId)
		{
			return func();
		}
		return framework.RunOnTick(func).GetAwaiter().GetResult();
	}

	#region core

	private (int, int) GetVersion() => (1, 0);

	private bool RefreshActors() {
		ctxManager.Current?.Scene.GetModule<ActorModule>().RefreshGPoseActors();
		return true;
	}

	private bool IsActive() => ctxManager.Current?.Posing.IsEnabled ?? false;

	private async Task<bool> LoadPose(uint index, string json, bool rotation, bool position, bool scale) {
		var transforms = PoseTransforms.None;
		if (rotation) transforms |= PoseTransforms.Rotation;
		if (position) transforms |= PoseTransforms.Position;
		if (scale) transforms |= PoseTransforms.Scale;

		return await LoadPose(index, json, transforms);
	}

	private async Task<bool> LoadPose(uint index, string json)
		=> await LoadPose(index, json, PoseTransforms.Rotation);

	private async Task<bool> LoadPose(uint index, string json, PoseTransforms transforms) {
		if (ctxManager.Current is null)
			return false;

		var file = JsonConvert.DeserializeObject<PoseFile>(json);
		var actor = ctxManager.Current.Scene.GetEntityForIndex(index);

		if (actor is null || file is null)
			return false;

		await ctxManager.Current.Posing.ApplyPoseFile(
			actor.Pose!,
			file,
			transforms: transforms
		);

		return true;
	}

	private async Task<string?> SavePose(uint index) {
		if (ctxManager.Current is null)
			return null;

		var actor = ctxManager.Current.Scene.GetEntityForIndex(index);
		if (actor?.Pose is null)
			return null;

		var file = await ctxManager.Current.Posing.SavePoseFile(actor.Pose);
		return JsonConvert.SerializeObject(file);
	}

	private async Task<Dictionary<int, HashSet<string>>> SelectedBones() {
		var sceneChildren = ctxManager.Current?.Scene?.Children
			.OfType<ActorEntity>()
			.ToList();

		if (sceneChildren is null || sceneChildren.Count == 0)
			return new();

		var ret = new Dictionary<int, HashSet<string>>();

		foreach (var actor in sceneChildren)
		{
			if (!actor.IsValid || actor.Pose is null)
				continue;

			ret[actor.Actor.ObjectIndex] =
				actor.Children.OfType<EntityPose>()
					.SelectMany(x => x.Recurse())
					.Where(s => s.IsSelected)
					.OfType<BoneNode>()
					.Select(s => s.Info.Name)
					.ToHashSet();
		}

		return ret;
	}

	#endregion

	#region Transform IPC

	private ActorEntity? GetEntity(uint index)
		=> ctxManager.Current?.Scene?.GetEntityForIndex(index);

	private BoneNode? GetParentBone(BoneNode bone)
		=> bone.Pose.Recurse().OfType<BoneNode>().FirstOrDefault(p => bone.IsBoneChildOf(p));

	private Transform CalculateWorldTransform(ActorEntity? actor, BoneNode bone, Transform inputTransform, bool inputIsWorldSpace) {
		if (inputIsWorldSpace) return inputTransform;

		Transform? parentWorld = null;

		var parent = GetParentBone(bone);
		if (parent != null)
		{
			parentWorld = parent.GetTransform();
		} else if (actor != null)
		{
			parentWorld = actor.GetTransform();
		}

		// can't find a parent context, return input as is
		if (parentWorld == null) return inputTransform;

		// ParentRot * LocalRot
		var newRot = Quaternion.Normalize(parentWorld.Rotation * inputTransform.Rotation);

		// ParentPos + (ParentRot * (LocalPos * ParentScale))
		var scaledLocalPos = inputTransform.Position * parentWorld.Scale;
		var rotatedLocalPos = Vector3.Transform(scaledLocalPos, parentWorld.Rotation);
		var newPos = parentWorld.Position + rotatedLocalPos;

		// Parent Scale * Local Scale to respect actor/parent scaling
		var newScale = inputTransform.Scale * parentWorld.Scale;

		return new Transform(newPos, newRot, newScale);
	}

	private Transform? GetBoneTransform(ActorEntity? actor, BoneNode bone, bool useWorldSpace) {
		var worldTransform = bone.GetTransform();
		if (worldTransform == null) return null;

		if (useWorldSpace) return worldTransform;

		Transform? parentWorld = null;

		var parent = GetParentBone(bone);
		if (parent != null)
		{
			parentWorld = parent.GetTransform();
		} else if (actor != null)
		{
			parentWorld = actor.GetTransform();
		}

		if (parentWorld == null) return worldTransform;

		// size of the bone in the world not relative to parent
		var localScale = worldTransform.Scale;

		// Inv(ParentRot) * WorldRot
		var invParentRot = Quaternion.Inverse(parentWorld.Rotation);
		var localRot = Quaternion.Normalize(invParentRot * worldTransform.Rotation);

		// Inv(ParentRot) * (WorldPos - ParentPos) / ParentScale
		// divide by ParentScale get correct relative distance
		var posDiff = worldTransform.Position - parentWorld.Position;
		var unrotatedPos = Vector3.Transform(posDiff, invParentRot);

		var pScale = parentWorld.Scale;
		var localPos = new Vector3(
			Math.Abs(pScale.X) > 0.0001f ? unrotatedPos.X / pScale.X : unrotatedPos.X,
			Math.Abs(pScale.Y) > 0.0001f ? unrotatedPos.Y / pScale.Y : unrotatedPos.Y,
			Math.Abs(pScale.Z) > 0.0001f ? unrotatedPos.Z / pScale.Z : unrotatedPos.Z
		);

		return new Transform(localPos, localRot, localScale);
	}

	private bool ApplyBoneTransform(IEditorContext ctx, BoneNode bone, Transform worldTarget) {
		var target = new TransformTarget(bone, new[] { bone });

		var action = ctx.Transform.Begin(target, setup => {
			setup.MirrorRotation = MirrorMode.Inverse;
			setup.ParentBones = true;
			setup.RelativeBones = true;
			setup.UseMatrixlessPropagation = true;
		});

		action.SetTransform(worldTarget);
		action.Dispatch();
		return true;
	}

	private Transform? GetTransform(uint index, string boneName, bool useWorldSpace) {
		return RunOnMainThread(() => {
			var actor = GetEntity(index);
			var bone = actor?.Pose?.FindBoneByName(boneName);
			if (bone is null) return null;
			return GetBoneTransform(actor, bone, useWorldSpace);
		});
	}

	private bool SetTransform(uint index, string boneName, Transform transform, bool useWorldSpace) {
		return RunOnMainThread(() => {
			var ctx = ctxManager.Current;
			var actor = GetEntity(index);
			var bone = actor?.Pose?.FindBoneByName(boneName);

			if (ctx is null || bone is null) return false;

			var worldTarget = CalculateWorldTransform(actor, bone, transform, useWorldSpace);
			return ApplyBoneTransform(ctx, bone, worldTarget);
		});
	}

	private Dictionary<string, Transform?> BatchGetTransform(uint index, List<string> names, bool useWorldSpace) {
		return RunOnMainThread(() => {
			var actor = GetEntity(index);
			var ret = new Dictionary<string, Transform?>();
			if (actor?.Pose == null) return ret;

			var allBones = actor.Pose.Recurse().OfType<BoneNode>().ToDictionary(b => b.Info.Name, b => b);

			foreach (var name in names)
			{
				if (!allBones.TryGetValue(name, out var bone))
				{
					ret[name] = null;
					continue;
				}
				ret[name] = GetBoneTransform(actor, bone, useWorldSpace);
			}
			return ret;
		});
	}

	private bool BatchSetTransform(uint index, Dictionary<string, Transform> transforms, bool useWorldSpace) {
		return RunOnMainThread(() => {
			var ctx = ctxManager.Current;
			var actor = GetEntity(index);
			if (ctx == null || actor?.Pose == null || transforms.Count == 0) return false;

			var bones = actor.Pose.Recurse().OfType<BoneNode>().ToList();

			bones.Sort((a, b) => {
				int p = a.Info.PartialIndex.CompareTo(b.Info.PartialIndex);
				return p != 0 ? p : a.Info.BoneIndex.CompareTo(b.Info.BoneIndex);
			});

			bool anySuccess = false;

			foreach (var bone in bones)
			{
				if (!transforms.TryGetValue(bone.Info.Name, out var transform))
					continue;

				var worldTarget = CalculateWorldTransform(actor, bone, transform, useWorldSpace);
				if (ApplyBoneTransform(ctx, bone, worldTarget))
					anySuccess = true;
			}

			return anySuccess;
		});
	}

	private Dictionary<string, Transform?> GetAllTransforms(uint index, bool useWorldSpace) {
		return RunOnMainThread(() => {
			var actor = GetEntity(index);
			var ret = new Dictionary<string, Transform?>();
			if (actor?.Pose == null) return ret;

			foreach (var bone in actor.Pose.Recurse().OfType<BoneNode>())
			{
				ret[bone.Info.Name] = GetBoneTransform(actor, bone, useWorldSpace);
			}
			return ret;
		});
	}

	#endregion

	public void RegisterIpc() {
		IpcVersion.RegisterFunc(GetVersion);
		IpcRefreshActions.RegisterFunc(RefreshActors);
		IpcIsPosing.RegisterFunc(IsActive);
		IpcLoadPose.RegisterFunc(LoadPose);
		IpcLoadPoseExtended.RegisterFunc(LoadPose);
		IpcSavePose.RegisterFunc(SavePose);
		IpcGetTransform.RegisterFunc(GetTransform);
		IpcSetTransform.RegisterFunc(SetTransform);
		IpcSelectedBones.RegisterFunc(SelectedBones);
		IpcBatchGetTransform.RegisterFunc(BatchGetTransform);
		IpcBatchSetTransform.RegisterFunc(BatchSetTransform);
		IpcGetAllTransforms.RegisterFunc(GetAllTransforms);
	}

	private void UnregisterIpc() {
		IpcVersion.UnregisterFunc();
		IpcRefreshActions.UnregisterFunc();
		IpcIsPosing.UnregisterFunc();
		IpcLoadPose.UnregisterFunc();
		IpcLoadPoseExtended.UnregisterFunc();
		IpcSavePose.UnregisterFunc();
		IpcGetTransform.UnregisterFunc();
		IpcSetTransform.UnregisterFunc();
		IpcSelectedBones.UnregisterFunc();
		IpcBatchGetTransform.UnregisterFunc();
		IpcBatchSetTransform.UnregisterFunc();
		IpcGetAllTransforms.UnregisterFunc();
	}

	public void Dispose() {
		Ktisis.Log.Info("Disposing Ktisis IPC Provider.");
		this.UnregisterIpc();
	}
}