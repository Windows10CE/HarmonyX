using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib.Tools;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace HarmonyLib.Public.Patching
{
	/// <summary>
	/// A method patcher that uses <see cref="MonoMod.RuntimeDetour.NativeDetour"/> to patch internal calls,
	/// methods marked with <see cref="DynDllImportAttribute"/> and any other managed method that CLR managed-to-native
	/// trampolines for and which has no IL body defined.
	/// </summary>
	public class NativeDetourMethodPatcher : MethodPatcher
	{
		private static readonly Dictionary<int, Delegate> TrampolineCache = new Dictionary<int, Delegate>();
		private static int counter;
		private static object counterLock = new object();

		private static readonly MethodInfo GetTrampolineMethod =
			AccessTools.Method(typeof(NativeDetourMethodPatcher), nameof(GetTrampoline));

		private string[] argTypeNames;
		private Type[] argTypes;

		private int currentOriginal, newOriginal;
		private MethodInfo invokeTrampolineMethod;
		private NativeDetour nativeDetour;
		private Type returnType;
		private Type trampolineDelegateType;

		public NativeDetourMethodPatcher(MethodBase original) : base(original)
		{
			Init();
		}

		private void Init()
		{
			if (AccessTools.IsNetCoreRuntime)
				Logger.Log(Logger.LogChannel.Warn, () => $"Patch target {Original.FullDescription()} is marked as extern. " +
				                                         "Extern methods may not be patched because of inlining behaviour of coreclr (refer to https://github.com/dotnet/coreclr/pull/8263)." +
				                                         "If you need to patch externs, consider using pure NativeDetour instead.");

			var orig = Original;

			var args = orig.GetParameters();
			var offs = orig.IsStatic ? 0 : 1;
			argTypes = new Type[args.Length + offs];
			argTypeNames = new string[args.Length + offs];
			returnType = (orig as MethodInfo)?.ReturnType;

			if (!orig.IsStatic)
			{
				argTypes[0] = orig.GetThisParamType();
				argTypeNames[0] = "this";
			}

			for (var i = 0; i < args.Length; i++)
			{
				argTypes[i + offs] = args[i].ParameterType;
				argTypeNames[i + offs] = args[i].Name;
			}

			trampolineDelegateType = DelegateTypeFactory.instance.CreateDelegateType(returnType, argTypes);
			invokeTrampolineMethod = AccessTools.Method(trampolineDelegateType, "Invoke");
		}

		/// <inheritdoc />
		public override DynamicMethodDefinition PrepareOriginal()
		{
			return GenerateManagedOriginal();
		}

		/// <inheritdoc />
		public override MethodBase DetourTo(MethodBase replacement)
		{
			nativeDetour?.Dispose();

			nativeDetour = new NativeDetour(Original, replacement, new NativeDetourConfig { ManualApply = true });

			lock (TrampolineCache)
			{
				TrampolineCache.Remove(currentOriginal);
				currentOriginal = newOriginal;
				TrampolineCache[currentOriginal] = CreateDelegate(trampolineDelegateType,
					nativeDetour.GenerateTrampoline(invokeTrampolineMethod));
			}

			nativeDetour.Apply();
			return replacement;
		}

		private Delegate CreateDelegate(Type delegateType, MethodBase mb)
		{
			if (mb is DynamicMethod dm)
				return dm.CreateDelegate(delegateType);

			return Delegate.CreateDelegate(delegateType, mb as MethodInfo ?? throw new InvalidCastException($"Unexpected method type: {mb.GetType()}"));
		}

		private static Delegate GetTrampoline(int hash)
		{
			lock (TrampolineCache)
				return TrampolineCache[hash];
		}

		private DynamicMethodDefinition GenerateManagedOriginal()
		{
			// Here we generate the "managed" version of the native method
			// It simply calls the trampoline generated by MonoMod
			// As a result, we can pass the managed original to HarmonyManipulator like a normal method

			var orig = Original;

			var dmd = new DynamicMethodDefinition($"NativeDetour<{orig.GetID(simple: true)}>", returnType, argTypes);
			lock (counterLock)
			{
				dmd.Definition.Name += $"?{counter}";
				newOriginal = counter;
				counter++;
			}

			var def = dmd.Definition;
			for (var i = 0; i < argTypeNames.Length; i++)
				def.Parameters[i].Name = argTypeNames[i];

			var il = dmd.GetILGenerator();

			il.Emit(OpCodes.Ldc_I4, dmd.GetHashCode());
			il.Emit(OpCodes.Call, GetTrampolineMethod);
			for (var i = 0; i < argTypes.Length; i++)
				il.Emit(OpCodes.Ldarg, i);
			il.Emit(OpCodes.Call, invokeTrampolineMethod);
			il.Emit(OpCodes.Ret);

			return dmd;
		}

		public static void TryResolve(object sender, PatchManager.PatcherResolverEeventArgs args)
		{
			if (args.Original.GetMethodBody() == null)
				args.MethodPatcher = new NativeDetourMethodPatcher(args.Original);
		}
	}
}
