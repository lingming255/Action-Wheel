using System;
using System.Collections.Generic;
using System.Linq;
using Action_Wheel.Services;

namespace Action_Wheel.Settings
{
    public sealed partial class SettingsWindow
    {
        private static void VerifyDirectToolActionTypes()
        {
            var model = new ActionEditModel(new ActionItem { Tag = 1 });
            var notified = new HashSet<string>();
            model.PropertyChanged += (sender, args) => notified.Add(args.PropertyName ?? string.Empty);
            foreach (var (index, function) in new[]
            {
                (6, WindowsFunctions.NoteBoard), (7, WindowsFunctions.QuickAdd),
            })
            {
                model.TypeSelectionIndex = index;
                var action = model.ToActionItem();
                Require(model.TypeSelectionIndex == index && model.KindIndex == ActionValueCodec.FunctionIndex
                    && action.Kind == ActionKind.Function && action.Value == function,
                    "The direct tool type did not produce its executable built-in action.");
                var configured = ActionConfig.Defaults().Select(item => item.Tag == 1 ? action : item).ToArray();
                var reloaded = new ActionEditModel(ActionConfig.Parse(ActionConfig.ToJson(configured)).Single(item => item.Tag == 1));
                Require(reloaded.TypeSelectionIndex == index,
                    "The tool type was lost after saving and reopening its configuration.");
                model.HoldTypeSelectionIndex = index;
                action = model.ToActionItem();
                Require(action.HoldKind == ActionKind.Function && action.HoldValue == function
                    && new ActionEditModel(action).HoldTypeSelectionIndex == index,
                    "The direct tool hold action did not survive a model reload.");

                // Switching back to the general function picker must leave that picker selected,
                // rather than projecting its old tool value straight back to the preset.
                model.TypeSelectionIndex = ActionValueCodec.FunctionIndex;
                model.HoldTypeSelectionIndex = ActionValueCodec.FunctionIndex;
                Require(model.TypeSelectionIndex == ActionValueCodec.FunctionIndex
                    && model.HoldTypeSelectionIndex == ActionValueCodec.FunctionIndex,
                    "A direct tool choice prevented switching back to Windows functions.");
            }
            model.Value = WindowsFunctions.NoteBoard;
            model.HoldValue = WindowsFunctions.QuickAdd;
            Require(model.TypeSelectionIndex == 6 && model.HoldTypeSelectionIndex == 7
                && notified.Contains(nameof(ActionEditModel.TypeSelectionIndex))
                && notified.Contains(nameof(ActionEditModel.HoldTypeSelectionIndex)),
                "Changing a function did not update the displayed type selection.");
            model.CopyFrom(new ActionItem
            {
                Tag = 1, Kind = ActionKind.Function, Value = WindowsFunctions.QuickAdd,
                HoldKind = ActionKind.Function, HoldValue = WindowsFunctions.NoteBoard,
            });
            Require(model.TypeSelectionIndex == 7 && model.HoldTypeSelectionIndex == 6,
                "Undo/reload did not restore the direct tool selections.");
            model.TypeSelectionIndex = ActionValueCodec.CopyTextIndex;
            model.Value = "A literal note";
            Require(model.ToActionItem().Kind == ActionKind.CopyText,
                "Adding tool choices changed the existing copy-text type index.");

            int boardRequests = 0, addRequests = 0;
            IntPtr observedTarget = IntPtr.Zero;
            EventHandler board = (sender, args) => boardRequests++;
            EventHandler<IntPtr> add = (sender, target) => { addRequests++; observedTarget = target; };
            ActionDispatcher.NoteBoardRequested += board;
            ActionDispatcher.QuickAddRequested += add;
            try
            {
                model.TypeSelectionIndex = 6;
                ActionDispatcher.Execute(model.ToActionItem());
                model.TypeSelectionIndex = 7;
                ActionDispatcher.Execute(model.ToActionItem(), new IntPtr(1234));
                Require(boardRequests == 1 && addRequests == 1 && observedTarget == new IntPtr(1234),
                    "Configured tool actions did not reach their window request handlers.");
            }
            finally
            {
                ActionDispatcher.NoteBoardRequested -= board;
                ActionDispatcher.QuickAddRequested -= add;
            }
        }
    }
}
