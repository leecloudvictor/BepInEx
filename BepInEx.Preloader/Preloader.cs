﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Patching;
using BepInEx.Preloader.RuntimeFixes;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using UnityInjector.ConsoleUtil;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace BepInEx.Preloader
{
	/// <summary>
	///     The main entrypoint of BepInEx, and initializes all patchers and the chainloader.
	/// </summary>
	internal static class Preloader
	{
		/// <summary>
		///     The log writer that is specific to the preloader.
		/// </summary>
		private static PreloaderConsoleListener PreloaderLog { get; set; }

		public static bool IsPostUnity2017 { get; } = File.Exists(Path.Combine(Paths.ManagedPath, "UnityEngine.CoreModule.dll"));

		public static void Run()
		{
			try
			{
				AllocateConsole();

				bool bridgeInitialized = Utility.TryDo(() =>
				{
					if (ConfigShimHarmony.Value)
						HarmonyDetourBridge.Init();
				}, out var harmonyBridgeException);

				Exception runtimePatchException = null;
				if(bridgeInitialized)
					Utility.TryDo(() =>
					{
						if (ConfigApplyRuntimePatches.Value)
							UnityPatches.Apply();
					}, out runtimePatchException);

				Logger.Sources.Add(TraceLogSource.CreateSource());

				PreloaderLog = new PreloaderConsoleListener(ConfigPreloaderCOutLogging.Value);
				Logger.Listeners.Add(PreloaderLog);

				string consoleTile = $"BepInEx {typeof(Paths).Assembly.GetName().Version} - {Process.GetCurrentProcess().ProcessName}";
				ConsoleWindow.Title = consoleTile;
				Logger.LogMessage(consoleTile);

				//See BuildInfoAttribute for more information about this section.
				object[] attributes = typeof(BuildInfoAttribute).Assembly.GetCustomAttributes(typeof(BuildInfoAttribute), false);

				if (attributes.Length > 0)
				{
					var attribute = (BuildInfoAttribute)attributes[0];
					Logger.LogMessage(attribute.Info);
				}

				Logger.LogInfo($"Running under Unity v{FileVersionInfo.GetVersionInfo(Paths.ExecutablePath).FileVersion}");
				Logger.LogInfo($"CLR runtime version: {Environment.Version}");
				Logger.LogInfo($"Supports SRE: {Utility.CLRSupportsDynamicAssemblies}");

				if (harmonyBridgeException != null)
					Logger.LogWarning($"Failed to enable fix for Harmony for .NET Standard API. Error message: {harmonyBridgeException.Message}");

				if (runtimePatchException != null)
					Logger.LogWarning($"Failed to apply runtime patches for Mono. See more info in the output log. Error message: {runtimePatchException.Message}");

				Logger.LogMessage("Preloader started");

				AssemblyPatcher.AddPatcher(new PatcherPlugin
				{
					TargetDLLs = () => new[] { ConfigEntrypointAssembly.Value },
					Patcher = PatchEntrypoint,
					TypeName = "BepInEx.Chainloader"
				});

				AssemblyPatcher.AddPatchersFromDirectory(Paths.PatcherPluginPath);

				Logger.LogInfo($"{AssemblyPatcher.PatcherPlugins.Count} patcher plugin(s) loaded");

				AssemblyPatcher.PatchAndLoad(Paths.ManagedPath);
				AssemblyPatcher.DisposePatchers();


				Logger.LogMessage("Preloader finished");

				Logger.Listeners.Remove(PreloaderLog);
				Logger.Listeners.Add(new ConsoleLogListener());

				PreloaderLog.Dispose();
			}
			catch (Exception ex)
			{
				File.WriteAllText("err.log", ex.ToString());
				try
				{
					Logger.LogFatal("Could not run preloader!");
					Logger.LogFatal(ex);

					PreloaderLog?.Dispose();

					if (!ConsoleWindow.IsAttached)
					{
						//if we've already attached the console, then the log will already be written to the console
						AllocateConsole();
						Console.Write(PreloaderLog);
					}

					PreloaderLog = null;
				}
				finally
				{
					File.WriteAllText(
						Path.Combine(Paths.GameRootPath, $"preloader_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log"),
						PreloaderLog + "\r\n" + ex);

					PreloaderLog?.Dispose();
					PreloaderLog = null;
				}
			}
		}

		/// <summary>
		///     Inserts BepInEx's own chainloader entrypoint into UnityEngine.
		/// </summary>
		/// <param name="assembly">The assembly that will be attempted to be patched.</param>
		public static void PatchEntrypoint(ref AssemblyDefinition assembly)
		{
			if (assembly.MainModule.AssemblyReferences.Any(x => x.Name.Contains("BepInEx")))
				throw new Exception("BepInEx has been detected to be patched! Please unpatch before using a patchless variant!");

			string entrypointType = ConfigEntrypointType.Value;
			string entrypointMethod = ConfigEntrypointMethod.Value;

			bool isCctor = entrypointMethod.IsNullOrWhiteSpace() || entrypointMethod == ".cctor";


			var entryType = assembly.MainModule.Types.FirstOrDefault(x => x.Name == entrypointType);

			if (entryType == null)
				throw new Exception("The entrypoint type is invalid! Please check your config.ini");

			using (var injected = AssemblyDefinition.ReadAssembly(Paths.BepInExAssemblyPath))
			{
				var originalInitMethod = injected.MainModule.Types.First(x => x.Name == "Chainloader").Methods
												 .First(x => x.Name == "Initialize");

				var originalStartMethod = injected.MainModule.Types.First(x => x.Name == "Chainloader").Methods
												  .First(x => x.Name == "Start");

				var initMethod = assembly.MainModule.ImportReference(originalInitMethod);
				var startMethod = assembly.MainModule.ImportReference(originalStartMethod);

				var methods = new List<MethodDefinition>();

				if (isCctor)
				{
					var cctor = entryType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);

					if (cctor == null)
					{
						cctor = new MethodDefinition(".cctor",
							MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig
							| MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
							assembly.MainModule.ImportReference(typeof(void)));

						entryType.Methods.Add(cctor);
						var il = cctor.Body.GetILProcessor();
						il.Append(il.Create(OpCodes.Ret));
					}

					methods.Add(cctor);
				}
				else
				{
					methods.AddRange(entryType.Methods.Where(x => x.Name == entrypointMethod));
				}

				if (!methods.Any())
					throw new Exception("The entrypoint method is invalid! Please check your config.ini");

				foreach (var method in methods)
				{
					var il = method.Body.GetILProcessor();

					var ins = il.Body.Instructions.First();

					il.InsertBefore(ins,
						il.Create(OpCodes.Ldnull)); // gameExePath (always null, we initialize the Paths class in Entrypoint
					il.InsertBefore(ins,
						il.Create(OpCodes.Ldc_I4_0)); //startConsole (always false, we already load the console in Preloader)
					il.InsertBefore(ins,
						il.Create(OpCodes.Call, initMethod)); // Chainloader.Initialize(string gamePath, string managedPath = null, bool startConsole = true)
					il.InsertBefore(ins,
						il.Create(OpCodes.Call, startMethod));
				}
			}
		}

		/// <summary>
		///     Allocates a console window for use by BepInEx safely.
		/// </summary>
		public static void AllocateConsole()
		{
			if (!ConsoleWindow.ConfigConsoleEnabled.Value)
				return;

			try
			{
				ConsoleWindow.Attach();

				var encoding = (uint)Encoding.UTF8.CodePage;

				if (ConsoleWindow.ConfigConsoleShiftJis.Value)
					encoding = 932;

				ConsoleEncoding.ConsoleCodePage = encoding;
				Console.OutputEncoding = ConsoleEncoding.GetEncoding(encoding);
			}
			catch (Exception ex)
			{
				Logger.LogError("Failed to allocate console!");
				Logger.LogError(ex);
			}
		}

		#region Config

		private static readonly ConfigWrapper<string> ConfigEntrypointAssembly = ConfigFile.CoreConfig.Wrap(
			"Preloader.Entrypoint",
			"Assembly",
			"The local filename of the assembly to target.",
			IsPostUnity2017 ? "UnityEngine.CoreModule.dll" : "UnityEngine.dll"
		);

		private static readonly ConfigWrapper<string> ConfigEntrypointType = ConfigFile.CoreConfig.Wrap(
			"Preloader.Entrypoint",
			"Type",
			"The name of the type in the entrypoint assembly to search for the entrypoint method.",
			"Application");

		private static readonly ConfigWrapper<string> ConfigEntrypointMethod = ConfigFile.CoreConfig.Wrap(
			"Preloader.Entrypoint",
			"Method",
			"The name of the method in the specified entrypoint assembly and type to hook and load Chainloader from.",
			".cctor");

		private static readonly ConfigWrapper<bool> ConfigApplyRuntimePatches = ConfigFile.CoreConfig.Wrap(
			"Preloader",
			"ApplyRuntimePatches",
			"Enables or disables runtime patches.\nThis should always be true, unless you cannot start the game due to a Harmony related issue (such as running .NET Standard runtime) or you know what you're doing.",
			true);

		private static readonly ConfigWrapper<bool> ConfigShimHarmony = ConfigFile.CoreConfig.Wrap(
			"Preloader",
			"ShimHarmonySupport",
			"If enabled, basic Harmony functionality is patched to use MonoMod's RuntimeDetour instead.\nTry using this if Harmony does not work in a game.",
			!Utility.CLRSupportsDynamicAssemblies);

		private static readonly ConfigWrapper<bool> ConfigPreloaderCOutLogging = ConfigFile.CoreConfig.Wrap(
			"Logging",
			"PreloaderConsoleOutRedirection",
			"Redirects text from Console.Out during preloader patch loading to the BepInEx logging system.",
			true);

		#endregion
	}
}