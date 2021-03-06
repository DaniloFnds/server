﻿/*
 * This file is part of Project Hybrasyl.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful, but
 * without ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
 * or FITNESS FOR A PARTICULAR PURPOSE. See the Affero General Public License
 * for more details.
 *
 * You should have received a copy of the Affero General Public License along
 * with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * (C) 2013 Justin Baugh (baughj@hybrasyl.com)
 * (C) 2015 Project Hybrasyl (info@hybrasyl.com)
 *
 * Authors:   Justin Baugh  <baughj@hybrasyl.com>
 *            Kyle Speck    <kojasou@hybrasyl.com>
 */

using Hybrasyl.Dialogs;
using Hybrasyl.Enums;
using Hybrasyl.Objects;
using Hybrasyl.Properties;
using IronPython.Hosting;
using IronPython.Runtime;
using IronPython.Runtime.Operations;
using log4net;
using Microsoft.Scripting.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Hybrasyl.Items;

namespace Hybrasyl
{

    public struct ScriptInvocation
    {
        public dynamic Function;
        public Script Script;
        public WorldObject Associate;
        public WorldObject Invoker;

        public ScriptInvocation(dynamic function = null, WorldObject associate = null, WorldObject invoker = null, Script script = null)
        {
            Function = function;
            Associate = associate;
            Invoker = invoker;
            Script = null;
        }

        public bool Execute(params object[] parameters)
        {
            if (Script != null)
                return Script.ExecuteFunction(this, parameters);
            else
                return Associate.Script.ExecuteFunction(this, parameters);
        }
    }

    public delegate void UpdateHandler();

    public class Script
    {
        public static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        public ScriptSource Source { get; set; }
        public string Name { get; set; }
        public string Path { get; private set; }

        public HybrasylScriptProcessor Processor { get; set; }
        public CompiledCode Compiled { get; private set; }
        public ScriptScope Scope { get; set; }
        public dynamic Instance { get; set; }
        public HybrasylWorldObject Associate { get; private set; }

        public bool Disabled { get; set; }
        public string CompilationError { get; private set; }
        public string LastRuntimeError { get; private set; }

        public Script Clone()
        {
            var clone = new Script(Path, Processor);
            // Reload and reinstantiate the script with a new ScriptScope
            Scope = Processor.Engine.CreateScope();
            clone.Load();
            clone.InstantiateScriptable();
            return clone;
        }
        
        public Script(string path, HybrasylScriptProcessor processor)
        {
            Path = path;
            Compiled = null;
            Source = null;
            Processor = processor;
            Disabled = false;
            CompilationError = string.Empty;
            LastRuntimeError = string.Empty;
        }

        public void AssociateScriptWithObject(WorldObject obj)
        {
            Associate = new HybrasylWorldObject(obj);
            obj.Script = this;
        }

        public dynamic GetObjectWrapper(WorldObject obj)
        {
            if (obj is User)
                return new HybrasylUser(obj as User);
            return new HybrasylWorldObject(obj);
        }
        /// <summary>
        /// Load the script from disk, recompile it into bytecode, and execute it.
        /// </summary>
        /// <returns>boolean indicating whether the script was reloaded or not</returns>
        public bool Load()
        {
            string scriptText;
            try
            {
                scriptText = File.ReadAllText(Path);
            }
            catch (Exception e)
            {
                Logger.ErrorFormat("Couldn't open script {0}: {1}", Path, e.ToString());
                Disabled = true;
                CompilationError = e.ToString();
                return false;
            }

            scriptText = //HybrasylScriptProcessor.RestrictStdlib 
                HybrasylScriptProcessor.HybrasylImports + scriptText;

            Source =
                Processor.Engine.CreateScriptSourceFromString(scriptText);

            Name = System.IO.Path.GetFileName(Path).ToLower();

            try
            {
                Compile();
                Compiled.Execute(Scope);
                Disabled = false;
            }
            catch (Exception e)
            {
                var pythonFrames = PythonOps.GetDynamicStackFrames(e);
                var exceptionString = Processor.Engine.GetService<ExceptionOperations>().FormatException(e);
                Logger.ErrorFormat("script {0} encountered error, Python stack follows", Path);
                Logger.ErrorFormat("{0}", exceptionString);
                Disabled = true;
                CompilationError = exceptionString;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Compile the script, using the global Hybrasyl engine.
        /// </summary>
        /// <returns>boolean indicating success or failure (might raise exception in the future)</returns>
        public bool Compile()
        {
            if (Source == null) return false;
            Compiled = Source.Compile();
            return true;
        }

        /// <summary>
        /// If the script has a Scriptable class (used for WorldObject hooks), instantiate it.
        /// </summary>
        public bool InstantiateScriptable()
        {
            // First, disable the script, then if we have an instance, delete it.
            Disabled = true;

            if (Instance != null)
                Instance = null;

            try
            {
                var klass = Scope.GetVariable("Scriptable");
                Scope.SetVariable("world", Processor.World);
                if (Associate != null)
                {
                    Scope.SetVariable("npc", Associate);
                    Associate.Obj.ResetPursuits();
                }
                Instance = Processor.Engine.Operations.CreateInstance(klass);
                Disabled = false;
            }
            catch (Exception e)
            {
                var pythonFrames = PythonOps.GetDynamicStackFrames(e);
                var exceptionString = Processor.Engine.GetService<ExceptionOperations>().FormatException(e);
                Logger.ErrorFormat("script {0} encountered error, Python stack follows", Path);
                Logger.ErrorFormat("{0}", exceptionString);
                Logger.ErrorFormat("script {0} now disabled", Path);
                Disabled = true;
                CompilationError = exceptionString;
                return false;
            }
            return true;
        }

        public bool ExecuteFunction(ScriptInvocation invocation, params object[] parameters)
        {
            if (Disabled)
                return false;

            if (!Processor.Engine.Operations.IsCallable(invocation.Function)) return false;
            if (invocation.Invoker is User)
            {
                Scope.SetVariable("invoker", new HybrasylUser(invocation.Invoker as User));
            }
            else
            {
                Scope.SetVariable("invoker", new HybrasylWorldObject(invocation.Invoker as WorldObject));
            }
            if (invocation.Associate is WorldObject)
            {
                Scope.SetVariable("npc", new HybrasylWorldObject(invocation.Associate as WorldObject));
            }
            try
            {
                var ret = Processor.Engine.Operations.Invoke(invocation.Function, parameters);
                if (ret is bool)
                    return (bool)ret;
            }
            catch (Exception e)
            {
                var pythonFrames = PythonOps.GetDynamicStackFrames(e);
                var exceptionString = Processor.Engine.GetService<ExceptionOperations>().FormatException(e);
                Logger.ErrorFormat("script {0} encountered error, Python stack follows", Path);
                Logger.ErrorFormat("{0}", exceptionString);
                Logger.ErrorFormat("script {0} now disabled", Path);
                LastRuntimeError = exceptionString;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Attach a Scriptable to an in game NPC.
        /// </summary>
        /// <returns></returns>
        public bool AttachScriptable(WorldObject obj)
        {
            Associate = new HybrasylWorldObject(obj);
            Logger.InfoFormat("Scriptable name: {0}", Instance.name);
            return true;
        }

        /// <summary>
        /// If the script has a Scriptable class and the given function exists, execute it.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="parameters">The parameters to pass to the function.</param>
        public void ExecuteScriptableFunction(string name, params object[] parameters)
        {
            if (Disabled)
                return;

            Scope.SetVariable("world", Processor.World);
            Scope.SetVariable("npc", Associate);

            try
            {
                Processor.Engine.Operations.InvokeMember(Instance, name, parameters);
            }
            catch (System.NotImplementedException)
            {
                Logger.DebugFormat("script {0}: missing member {1}", Path, name);
            }
            catch (Exception e)
            {
                var pythonFrames = PythonOps.GetDynamicStackFrames(e);
                var exceptionString = Processor.Engine.GetService<ExceptionOperations>().FormatException(e);
                Logger.ErrorFormat("script {0} encountered error, Python stack follows", Path);
                Logger.ErrorFormat("{0}", exceptionString);
                Logger.ErrorFormat("script {0} now disabled");
                LastRuntimeError = exceptionString;
            }
        }

        /// <summary>
        /// Execute the script in the passed scope.
        /// </summary>
        /// <param name="scope">The ScriptScope the script will execute in.</param>
        public void ExecuteScript(WorldObject caller = null)
        {
            dynamic resolvedCaller;

            if (caller != null)
            {
                if (caller is User)
                    resolvedCaller = new HybrasylUser(caller as User);
                else
                    resolvedCaller = new HybrasylWorldObject(caller);
                Scope.SetVariable("npc", resolvedCaller);
            }

            Scope.SetVariable("world", Processor.World);
            Compiled.Execute(Scope);
        }

    }

    public class HybrasylScriptProcessor
    {
        public static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public ScriptEngine Engine { get; private set; }
        public Dictionary<string, Script> Scripts { get; private set; }
        public HybrasylWorld World { get; private set; }

        // We make an attempt to limit Hybrasyl scripts to stdlib,
        // excluding "dangerous" functions (imports are disallowed outright,
        // along with file i/o or eval/exec/
        public static readonly string RestrictStdlib = 
        @"__builtins__.__import__ = None
__builtins__.reload = None
__builtins__.open = None 
__builtins__.eval = None
__builtins__.compile = None
__builtins__.execfile = None
__builtins__.file = None
__builtins__.memoryview = None
__builtins__.raw_input = None

";

        public static readonly string HybrasylImports =
            @"import clr
clr.AddReference('Hybrasyl')
from Hybrasyl.Enums import *
from System import DateTime

";

        public HybrasylScriptProcessor(World world)
        {
            Engine = Python.CreateEngine();
            var paths = Engine.GetSearchPaths();
            // FIXME: obvious
            paths.Add(@"C:\Program Files (x86)\IronPython 2.7\Lib");
            paths.Add(@"C:\Python27\Lib");
            Engine.SetSearchPaths(paths);
            Engine.ImportModule("random");

            Scripts = new Dictionary<string, Script>();
            World = new HybrasylWorld(world);
        }

        public bool TryGetScript(string scriptName, out Script script)
        {
            // Try to find "name.py" or "name"
            if (Scripts.TryGetValue($"{scriptName.ToLower()}.py", out script))
            {
                return true;
            }
            return Scripts.TryGetValue(scriptName.ToLower(), out script);
        }

        public Script GetScript(string scriptName)
        {
            Script script;
            // Try to find "name.py" or "name"
            var exists = Scripts.TryGetValue($"{scriptName.ToLower()}.py", out script);
            if (!exists)
            {
                if (Scripts.TryGetValue($"{scriptName.ToLower()}", out script))
                    return script;
            }
            else
                return script;
            return null;
        }

        public bool RegisterScript(Script script)
        {
            script.Scope = Engine.CreateScope();
            script.Load();
            Scripts[script.Name] = script;

            if (script.Disabled)
            {
                Logger.ErrorFormat("{0}: error loading script", script.Name);
                return false;
            }
            else
            {
                Logger.InfoFormat("{0}: loaded successfully", script.Name);
                return true;
            }
        }

        public bool DeregisterScript(string scriptname)
        {
            Scripts[scriptname] = null;
            return true;
        }
    }

    // We define some wrapper classes here, which we actually use to expose our scripting API.
    // In theory this is faster than isolation with an AppDomain, since we aren't serializing anything,
    // and adding / changing to the exposed API is as simple as modifying these classes. I'm not entirely
    // sure this is the best approach but it's what we're going with originally as it's the simplest (and I
    // believe, fastest) approach.

    public class HybrasylDialog
    {
        internal Dialog Dialog { get; set; }
        internal DialogSequence Sequence { get; set; }

        public HybrasylDialog(Dialog dialog)
        {
            Dialog = dialog;
        }

        public void SetNpcDisplaySprite(int displaySprite)
        {
            Dialog.DisplaySprite = (ushort)(0x4000 + displaySprite);
        }

        public void SetItemDisplaySprite(int displaySprite)
        {
            Dialog.DisplaySprite = (ushort)(0x8000 + displaySprite);
        }

        public void AssociateDialogWithSequence(DialogSequence sequence)
        {
            Sequence = sequence;
            sequence.AddDialog(Dialog);
        }

    }

    public class HybrasylDialogSequence
    {
        internal DialogSequence Sequence { get; private set; }

        public HybrasylDialogSequence(string sequenceName)
        {
            Sequence = new DialogSequence(sequenceName);
        }

        public void AddDialog(HybrasylDialog scriptDialog)
        {
            scriptDialog.AssociateDialogWithSequence(Sequence);
        }

        public void AddCheck(dynamic check)
        {
            Sequence.AddPreDisplayCallback(check);
        }
    }

    public class HybrasylWorld
    {
        public static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        internal World World { get; set; }

        public HybrasylWorld(World world)
        {
            World = world;
        }

        public HybrasylDialogSequence NewDialogSequence(string sequenceName, params object[] list)
        {
            var dialogSequence = new HybrasylDialogSequence(sequenceName);
            foreach (var entry in list)
            {
                Logger.InfoFormat("Type is {0}", entry.GetType().ToString());
                if (entry is HybrasylDialog)
                {
                    var newdialog = entry as HybrasylDialog;
                    dialogSequence.AddDialog(newdialog);
                }
                else if (entry is PythonFunction)
                {
                    var action = entry as PythonFunction;
                }

            }
            return dialogSequence;
        }

        public HybrasylDialog NewDialog(string displayText, dynamic callback = null)
        {
            var dialog = new SimpleDialog(displayText);
            dialog.SetCallbackHandler(callback);
            return new HybrasylDialog(dialog);
        }

        public HybrasylDialog NewTextDialog(string displayText, string topCaption, string bottomCaption, int inputLength = 254, dynamic handler = null, dynamic callback = null)
        {
            var dialog = new TextDialog(displayText, topCaption, bottomCaption, inputLength);
            dialog.setInputHandler(handler);
            dialog.SetCallbackHandler(callback);
            return new HybrasylDialog(dialog);
        }

        public HybrasylDialog NewOptionsDialog(string displayText, dynamic optionsStructure, dynamic handler = null, dynamic callback = null)
        {
            var dialog = new OptionsDialog(displayText);
            dialog.SetCallbackHandler(callback);

            if (optionsStructure is IronPython.Runtime.List)
            {
                // A simple options dialog with a callback handler for the response
                var optionlist = optionsStructure as IronPython.Runtime.List;
                foreach (var option in optionsStructure)
                {
                    if (option is string)
                    {
                        dialog.AddDialogOption(option as string);
                    }
                }
                if (handler != null)
                {
                    dialog.setInputHandler(handler);
                    Logger.InfoFormat("Input handler associated with dialog");
                }
            }
            else if (optionsStructure is IronPython.Runtime.PythonDictionary)
            {
                var hash = optionsStructure as IronPython.Runtime.PythonDictionary;
                foreach (var key in hash.Keys)
                {
                    if (key is string)
                    {
                        dialog.AddDialogOption(key as string, hash[key]);
                    }
                }

            }
            return new HybrasylDialog(dialog);
        }
    }

    public class HybrasylMap
    {
        private Map Map { get; set; }

        public HybrasylMap(Map map)
        {
            Map = map;
        }

        public bool DropItem(string name, int x = -1, int y = -1)
        {
            return false;
        }

    }

    public class HybrasylUser
    {
        public static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        internal User User { get; set; }
        internal HybrasylWorld World { get; set; }
        internal HybrasylMap Map { get; set; }
        public string Name => User.Name;

        public HybrasylUser(User user)
        {
            User = user;
            World = new HybrasylWorld(user.World);
            Map = new HybrasylMap(user.Map);
        }

        public List<HybrasylWorldObject> GetViewportObjects()
        {
            return new List<HybrasylWorldObject>();
        }

        public List<HybrasylUser> GetViewportPlayers()
        {
            return new List<HybrasylUser>();
        }

        public void Resurrect()
        {
            User.Resurrect();
        }

        public HybrasylUser GetFacingUser()
        {
            var facing = User.GetFacingUser();
            return facing != null ? new HybrasylUser(facing) : null;
        }

        public List<HybrasylWorldObject> GetFacingObjects()
        {
            return User.GetFacingObjects().Select(item => new HybrasylWorldObject(item)).ToList();
        }

        public void EndComa()
        {
            User.EndComa();
        }

        public dynamic GetLegendMark(string prefix)
        {
            LegendMark mark;
            return User.Legend.TryGetMark(prefix, out mark) ? mark : (object)null;
        }

        public Legend GetLegend()
        {
            return User.Legend;
        }

        public bool AddLegendMark(LegendIcon icon, LegendColor color, string text, DateTime created, string prefix=default(string), bool isPublic = true, int quantity = 0)
        {
            try
            {
                return User.Legend.AddMark(icon, color, text, created, prefix, isPublic, quantity);
            }
            catch (ArgumentException)
            {
                Logger.ErrorFormat("Legend mark: {0}: duplicate prefix {1}", User.Name, prefix);               
            }
            return false;
        }

        public bool RemoveLegendMark(string prefix)
        {
            return User.Legend.RemoveMark(prefix);
        }

        public bool ModifyLegendMark(string prefix, int quantity, bool isPublic)
        {
            LegendMark mark;
            if (!User.Legend.TryGetMark(prefix, out mark)) return false;
            mark.Quantity = quantity;
            mark.Public = isPublic;
            return true;
        }

        public void SetSessionFlag(string flag, dynamic value)
        {
            try
            {
                User.SetFlag(flag, value.ToString());
                Logger.DebugFormat("{0} - set flag {1} to {2}", User.Name, flag, value.toString());
            }
            catch (Exception e)
            {
                Logger.WarnFormat("{0}: value could not be converted to string? {1}", User.Name, e.ToString());
            }
        }

        public void SetFlag(string flag, dynamic value)
        {
            try
            {
                User.SetFlag(flag, value.ToString());
            }
            catch (Exception e)
            {
                Logger.WarnFormat("{0}: value could not be converted to string? {1}", User.Name, e.ToString());
            }

        }

        public string GetSessionFlag(string flag)
        {
            return User.GetSessionFlag(flag);
        }

        public string GetFlag(string flag)
        {
            return User.GetFlag(flag);
        }

        public void DisplayEffect(ushort effect, short speed = 100, bool global = true)
        {
            if (!global)
                User.SendEffect(User.Id, effect, speed);
            else
                User.Effect(effect, speed);
        }

        public void DisplayEffectAtCoords(short x, short y, ushort effect, short speed = 100, bool global = true)
        {
            if (!global)
                User.SendEffect(x, y, effect, speed);
            else
                User.Effect(x, y, effect, speed);
        }

        public void Teleport(String location, int x, int y)
        {
            User.Teleport(location, (byte) x, (byte) y);
        }

        public void SoundEffect(byte sound)
        {
            User.SendSound(sound);
        }

        public void HealToFull()
        {
            User.Heal(User.MaximumHp);
        }

        public void Heal(int heal)
        {
            User.Heal((double)heal);            
        }

        public void Damage(int damage, Enums.Element element = Enums.Element.None,
           Enums.DamageType damageType = Enums.DamageType.Direct)
        {
            User.Damage((double) damage, element, damageType);
        }

        public bool GiveItem(string name)
        {
            // Does the item exist?
            Item theitem;
            if (Game.World.ItemCatalog.TryGetValue(new Tuple<Sex, string>(User.Sex, name), out theitem) ||
                Game.World.ItemCatalog.TryGetValue(new Tuple<Sex, string>(Sex.Neutral, name), out theitem))
            {
                Logger.DebugFormat("giving item {0} to {1}", name, User.Name);
                var itemobj = Game.World.CreateItem(theitem.Id);
                Game.World.Insert(itemobj);
                User.AddItem(itemobj);
                return true;
            }
            else
            {
                Logger.DebugFormat("item {0} cannot be found", name);
            }
            return false;
        }

        public bool TakeItem(string name)
        {
            return false;
        }

        public bool GiveExperience(int exp)
        {
            SystemMessage($"{exp} experience!");
            User.GiveExperience((uint)exp);
            return true;
        }

        public bool TakeExperience(int exp)
        {
            User.Experience -= (uint)exp;
            SystemMessage($"Your world spins as your insight leaves you ((-{exp} experience!))");
            User.UpdateAttributes(StatUpdateFlags.Experience);
            return true;
        }

        public void SystemMessage(string message)
        {
            // This is a typical client "orange message"
            User.SendMessage(message, Hybrasyl.MessageTypes.SYSTEM_WITH_OVERHEAD);
        }


        public void Whisper(string name, string message)
        {
            User.SendWhisper(name, message);
        }

        public void Mail(string name, string message)
        {
        }

        public void StartDialogSequence(string sequenceName, HybrasylWorldObject associate)
        {
            DialogSequence newSequence;
            if (User.World.GlobalSequencesCatalog.TryGetValue(sequenceName, out newSequence))
            {
                newSequence.ShowTo(User, (VisibleObject)associate.Obj);
                // End previous sequence
                User.DialogState.EndDialog();
                User.DialogState.StartDialog(associate.Obj as VisibleObject, newSequence);
            }

        }

        public void StartSequence(string sequenceName, HybrasylWorldObject associateOverride = null)
        {
            DialogSequence sequence;
            VisibleObject associate;
            Logger.DebugFormat("{0} starting sequence {1}", User.Name, sequenceName);

            // If we're using a new associate, we will consult that to find our sequence
            associate = associateOverride == null ? User.DialogState.Associate as VisibleObject : associateOverride.Obj as VisibleObject;

            // Use the local catalog for sequences first, then consult the global catalog

            if (!associate.SequenceCatalog.TryGetValue(sequenceName, out sequence))
            {
                if (!User.World.GlobalSequencesCatalog.TryGetValue(sequenceName, out sequence))
                {
                    Logger.ErrorFormat("called from {0}: sequence name {1} cannot be found!",
                        associate.Name, sequenceName);
                    // To be safe, end all dialogs and basically abort
                    User.DialogState.EndDialog();
                    return;
                }
            }

            // sequence should now be our target sequence, let's end the current state and start a new one

            User.DialogState.EndDialog();
            User.DialogState.StartDialog(associate, sequence);
            User.DialogState.ActiveDialog.ShowTo(User, associate);
        }

    }

    public class HybrasylWorldObject
    {
        internal WorldObject Obj { get; set; }

        public HybrasylWorldObject(WorldObject obj)
        {
            Obj = obj;
        }

        public void DisplayPursuits(dynamic invoker)
        {
            if (Obj is Merchant)
            {
                var merchant = Obj as Merchant;
                if (invoker is HybrasylUser)
                {
                    var hybUser = (HybrasylUser) invoker;
                    merchant.DisplayPursuits(hybUser.User);
                }
            }

        }

        public void Destroy()
        {
            if (Obj is ItemObject || Obj is Gold)
            {
                Game.World.Remove(Obj);
            }
        }

        public void AddPursuit(HybrasylDialogSequence hybrasylSequence)
        {
            if (Obj is VisibleObject && !(Obj is User))
            {
                var vobj = Obj as VisibleObject;
                vobj.AddPursuit(hybrasylSequence.Sequence);
            }

        }

        public void RegisterSequence(HybrasylDialogSequence hybrasylSequence)
        {
            if (Obj is VisibleObject && !(Obj is User))
            {
                var vobj = Obj as VisibleObject;
                vobj.RegisterDialogSequence(hybrasylSequence.Sequence);
            }
        }

        public void RegisterGlobalSequence(HybrasylDialogSequence globalSequence)
        {
            Game.World.RegisterGlobalSequence(globalSequence.Sequence);
        }

        public void Say(string message)
        {
            if (Obj is Creature)
            {
                var creature = Obj as Creature;
                creature.Say(message);
            }
        }

    }

}
