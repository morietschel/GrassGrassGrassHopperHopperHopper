using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using HelloRhinoCommon.Models;
using Rhino;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.UI;

namespace HelloRhinoCommon.UI;

[Guid("9D0B9A10-3A8E-46FA-B6C2-4E004A80A29B")]
public sealed class ModifierStackPanel : Panel
{
    private const int MaxSliderResolution = 10000;
    private const int LinkButtonWidth = 32;

    private readonly Label _statusLabel;
    private readonly Scrollable _rowsScrollable;
    private readonly DropDown _definitionPicker;
    private readonly Button _addButton;
    private readonly Button _refreshButton;
    private readonly List<string> _importedDefinitionPaths = new();
    private readonly List<DefinitionChoice> _definitionChoices = new();
    private readonly HashSet<string> _expandedStepKeys = new(StringComparer.Ordinal);

    private bool _isUpdatingDefinitionPicker;

    private enum InputEditorKind
    {
        Text,
        Number,
        Slider,
        Toggle,
        Point,
        Geometry,
    }

    private readonly record struct NumericSliderConfiguration(double Minimum, double Maximum, int Steps, int DecimalPlaces)
    {
        public double GetValue(int sliderValue)
        {
            if (Steps <= 0 || Maximum <= Minimum)
            {
                return Minimum;
            }

            var clampedValue = Math.Clamp(sliderValue, 0, Steps);
            var progress = (double)clampedValue / Steps;
            return RoundNumber(Minimum + ((Maximum - Minimum) * progress), DecimalPlaces);
        }

        public int GetSliderValue(double actualValue)
        {
            if (Steps <= 0 || Maximum <= Minimum)
            {
                return 0;
            }

            var clampedValue = Math.Clamp(actualValue, Minimum, Maximum);
            var progress = (clampedValue - Minimum) / (Maximum - Minimum);
            return (int)Math.Round(progress * Steps, MidpointRounding.AwayFromZero);
        }
    }

    private readonly record struct DefinitionChoice(string FullPath, string DisplayName);

    public ModifierStackPanel()
    {
        _definitionPicker = new DropDown();
        _definitionPicker.SelectedIndexChanged += OnDefinitionSelected;

        _addButton = new Button
        {
            Text = "+",
            ToolTip = "Add a modifier from file.",
            Width = 44,
        };
        _addButton.Click += OnAddClicked;

        _refreshButton = new Button
        {
            Text = "↻",
            ToolTip = "Refresh the current modifier stack.",
            Width = 44,
        };
        _refreshButton.Click += OnRefreshClicked;

        _statusLabel = new Label
        {
            Text = string.Empty,
            Wrap = WrapMode.Word,
            Visible = false,
        };

        _rowsScrollable = new Scrollable
        {
            ExpandContentWidth = true,
        };

        Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = 12,
            Spacing = 12,
            Items =
            {
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Items =
                    {
                        new StackLayoutItem(_definitionPicker, true),
                        _addButton,
                        _refreshButton,
                    },
                },
                _statusLabel,
                new StackLayoutItem(_rowsScrollable, true),
            },
        };

        HelloRhinoCommonPlugin.Instance.Engine.StateChanged += OnEngineStateChanged;
        RefreshView();
    }

    public void RefreshNow()
    {
        RefreshView();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HelloRhinoCommonPlugin.Instance.Engine.StateChanged -= OnEngineStateChanged;
        }

        base.Dispose(disposing);
    }

    private void OnEngineStateChanged(object? sender, EventArgs e)
    {
        Application.Instance?.AsyncInvoke(RefreshView);
    }

    private void OnAddClicked(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var state = HelloRhinoCommonPlugin.Instance.Engine.GetPanelState(doc);
        if (!state.CanEdit || !state.SelectedObjectId.HasValue || doc is null)
        {
            return;
        }

        using var dialog = new Eto.Forms.OpenFileDialog
        {
            Title = "Add Grasshopper Modifier",
            MultiSelect = false,
        };
        dialog.Filters.Add(new FileFilter("Grasshopper Definitions", ".gh", ".ghx"));

        if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        TryAddStep(state, dialog.FileName);
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.RefreshSelectedObject(doc, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private void OnDefinitionSelected(object? sender, EventArgs e)
    {
        if (_isUpdatingDefinitionPicker)
        {
            return;
        }

        var selectedIndex = _definitionPicker.SelectedIndex;
        ResetDefinitionPicker();

        if (selectedIndex <= 0 || selectedIndex > _definitionChoices.Count)
        {
            return;
        }

        var state = HelloRhinoCommonPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        if (!state.CanEdit || !state.SelectedObjectId.HasValue)
        {
            return;
        }

        TryAddStep(state, _definitionChoices[selectedIndex - 1].FullPath);
    }

    private void RefreshView()
    {
        var state = HelloRhinoCommonPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);

        SyncDefinitionChoices(state);
        NormalizeExpandedSteps(state);

        var canEdit = state.CanEdit && state.SelectedObjectId.HasValue;
        var canRefresh = canEdit && state.Steps.Count > 0;
        _definitionPicker.Enabled = canEdit && _definitionChoices.Count > 0;
        _addButton.Enabled = canEdit;
        _refreshButton.Enabled = canRefresh;

        _statusLabel.Visible = canEdit && !string.IsNullOrWhiteSpace(state.StatusMessage);
        _statusLabel.Text = state.StatusMessage;

        var rows = new TableLayout
        {
            Spacing = new Size(0, 0),
        };

        if (!state.CanEdit)
        {
            rows.Rows.Add(new TableRow(new TableCell(CreateCenteredMessage(string.IsNullOrWhiteSpace(state.StatusMessage)
                ? "Select one object to edit its modifier stack."
                : state.StatusMessage), true)));
        }
        else if (state.Steps.Count == 0)
        {
            rows.Rows.Add(new TableRow(new TableCell(CreateCenteredMessage("No modifiers added yet.\n\nSearch above, or add a modifier\nfrom file to begin"), true)));
        }
        else if (state.SelectedObjectId.HasValue)
        {
            for (var i = 0; i < state.Steps.Count; i++)
            {
                var step = state.Steps[i];
                rows.Rows.Add(new TableRow(new TableCell(CreateStepRow(
                    state.SelectedObjectId.Value,
                    step,
                    canMoveUp: i > 0,
                    canMoveDown: i < state.Steps.Count - 1), true)));
            }
        }

        // Add a spacer row to push content to the top
        rows.Rows.Add(new TableRow { ScaleHeight = true });

        _rowsScrollable.Content = rows;
    }

    private bool TryAddStep(ModifierPanelState state, string path)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return false;
        }

        if (!state.SelectedObjectId.HasValue)
        {
            MessageBox.Show("Select an object first to add a modifier step.", MessageBoxType.Warning);
            return false;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.AddStep(doc, state.SelectedObjectId.Value, path, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return false;
        }

        RememberImportedDefinitions(path);

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }

        RefreshView();
        return true;
    }

    private void RememberImportedDefinitions(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var pathsToRemember = new List<string> { path };
        var folder = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                pathsToRemember.AddRange(Directory.EnumerateFiles(folder, "*.gh", SearchOption.AllDirectories));
                pathsToRemember.AddRange(Directory.EnumerateFiles(folder, "*.ghx", SearchOption.AllDirectories));
            }
            catch
            {
                // Keep the chosen file even if scanning the folder fails.
            }
        }

        foreach (var candidate in pathsToRemember.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            if (_importedDefinitionPaths.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _importedDefinitionPaths.Add(candidate);
        }

        _importedDefinitionPaths.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private void SyncDefinitionChoices(ModifierPanelState state)
    {
        foreach (var path in state.Steps
                     .Select(step => step.FullPath)
                     .Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (_importedDefinitionPaths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _importedDefinitionPaths.Add(path);
        }

        var uniquePaths = _importedDefinitionPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateNames = uniquePaths
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _definitionChoices.Clear();
        foreach (var path in uniquePaths)
        {
            _definitionChoices.Add(new DefinitionChoice(path, BuildDefinitionDisplayName(path, duplicateNames)));
        }

        _isUpdatingDefinitionPicker = true;
        _definitionPicker.DataStore = new[] { "Add Modifier..." }
            .Concat(_definitionChoices.Select(choice => choice.DisplayName))
            .ToList();
        _definitionPicker.SelectedIndex = 0;
        _isUpdatingDefinitionPicker = false;
    }

    private static string BuildDefinitionDisplayName(string path, ISet<string> duplicateNames)
    {
        var fileName = Path.GetFileName(path);
        if (!duplicateNames.Contains(fileName))
        {
            return fileName;
        }

        var folderName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        return string.IsNullOrWhiteSpace(folderName)
            ? fileName
            : $"{fileName} ({folderName})";
    }

    private void ResetDefinitionPicker()
    {
        _isUpdatingDefinitionPicker = true;
        _definitionPicker.SelectedIndex = 0;
        _isUpdatingDefinitionPicker = false;
    }

    private void NormalizeExpandedSteps(ModifierPanelState state)
    {
        var validKeys = state.Steps.Select(GetStepKey).ToHashSet(StringComparer.Ordinal);
        _expandedStepKeys.RemoveWhere(key => !validKeys.Contains(key));

        if (_expandedStepKeys.Count > 0)
        {
            return;
        }

        var firstProblemStep = state.Steps.FirstOrDefault(step => !string.IsNullOrWhiteSpace(step.ErrorMessage));
        if (firstProblemStep is not null)
        {
            _expandedStepKeys.Add(GetStepKey(firstProblemStep));
        }
    }

    private static string GetStepKey(ModifierStepPanelState step)
    {
        return step.StepId != Guid.Empty
            ? step.StepId.ToString("N")
            : $"{step.Index}:{step.FullPath}";
    }

    private bool IsStepExpanded(ModifierStepPanelState step)
    {
        return _expandedStepKeys.Contains(GetStepKey(step));
    }

    private void ToggleExpanded(ModifierStepPanelState step)
    {
        var stepKey = GetStepKey(step);
        if (_expandedStepKeys.Contains(stepKey))
        {
            _expandedStepKeys.Remove(stepKey);
        }
        else
        {
            _expandedStepKeys.Clear();
            _expandedStepKeys.Add(stepKey);
        }

        RefreshView();
    }

    private Control CreateStepRow(Guid objectId, ModifierStepPanelState step, bool canMoveUp, bool canMoveDown)
    {
        var isExpanded = IsStepExpanded(step);
        var container = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
        };

        // Header row: disclosure triangle + checkbox + name (layer-panel style)
        var disclosureLabel = new Label
        {
            Text = isExpanded ? "▾" : "▸",
            Width = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var actionButtons = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Items =
            {
                CreateSmallButton("✎", "Edit script", (_, _) => EditStepDefinition(step.FullPath), !string.IsNullOrWhiteSpace(step.FullPath)),
                CreateSmallButton("↑", "Move up", (_, _) => MoveStep(objectId, step.Index, -1), canMoveUp),
                CreateSmallButton("↓", "Move down", (_, _) => MoveStep(objectId, step.Index, 1), canMoveDown),
                CreateSmallButton("✕", "Remove", (_, _) => RemoveStep(objectId, step.Index)),
            },
        };

        var headerRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Padding(0, 3),
            Items =
            {
                disclosureLabel,
                CreateStepEnabledCheckBox(objectId, step),
                new StackLayoutItem(CreateStepNameLabel(step), true),
                actionButtons,
            },
        };

        // Click the disclosure triangle to toggle
        disclosureLabel.MouseDown += (_, _) => ToggleExpanded(step);

        container.Items.Add(new StackLayoutItem(headerRow, HorizontalAlignment.Stretch));

        // Separator line
        container.Items.Add(new StackLayoutItem(new Panel
        {
            Height = 1,
            BackgroundColor = Colors.LightGrey,
        }, HorizontalAlignment.Stretch));

        if (!string.IsNullOrWhiteSpace(step.ErrorMessage))
        {
            container.Items.Add(new StackLayout
            {
                Padding = new Padding(20, 2, 0, 0),
                Items = { CreateMessageLabel(step.ErrorMessage, isError: true) },
            });
        }

        // Expanded content: inputs indented like sub-layers
        if (isExpanded)
        {
            var childContent = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                Padding = new Padding(20, 6, 0, 6),
            };

            foreach (var input in step.Inputs)
            {
                childContent.Items.Add(CreateInputRow(objectId, step, input));
            }

            if (step.Outputs.Count > 0)
            {
                childContent.Items.Add(CreateOutputSummaryRow(step.Outputs));
            }

            container.Items.Add(childContent);
        }

        return container;
    }

    private static CheckBox CreateStepEnabledCheckBox(Guid objectId, ModifierStepPanelState step)
    {
        var enabledCheckBox = new CheckBox
        {
            Checked = step.Enabled,
        };

        enabledCheckBox.CheckedChanged += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            HelloRhinoCommonPlugin.Instance.Engine.SetStepEnabled(
                doc,
                objectId,
                step.Index,
                enabledCheckBox.Checked == true,
                out var message);

            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        };

        return enabledCheckBox;
    }

    private static Label CreateStepNameLabel(ModifierStepPanelState step)
    {
        return new Label
        {
            Text = step.DisplayName,
            ToolTip = step.FullPath,
            Wrap = WrapMode.Word,
        };
    }

    private static Button CreateSmallButton(string text, string toolTip, EventHandler<EventArgs> onClick, bool enabled = true)
    {
        var button = new Button
        {
            Text = text,
            ToolTip = toolTip,
            Enabled = enabled,
            Width = 26,
            Height = 22,
        };
        button.Click += onClick;
        return button;
    }

    private static Label CreateMessageLabel(string text, bool isError)
    {
        return new Label
        {
            Text = text,
            Wrap = WrapMode.Word,
            TextColor = isError ? Eto.Drawing.Colors.OrangeRed : Eto.Drawing.Colors.Gray,
        };
    }

    private static Control CreateCenteredMessage(string text)
    {
        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = new Padding(16, 56, 16, 0),
            Items =
            {
                new Label
                {
                    Text = text,
                    Wrap = WrapMode.Word,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };
    }

    private Control CreateInputRow(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var toolTip = BuildInputToolTip(input);
        var editorKind = GetEditorKind(input);

        return editorKind switch
        {
            InputEditorKind.Toggle => CreateToggleInputRow(objectId, step, input, toolTip),
            InputEditorKind.Slider => CreateInputBlock(objectId, step, input, CreateSliderEditor(objectId, step, input), toolTip),
            InputEditorKind.Number => CreateInputBlock(objectId, step, input, CreateNumericEditor(objectId, step, input), toolTip),
            InputEditorKind.Point => CreateInputBlock(objectId, step, input, CreatePointEditor(objectId, step, input), AppendToolTip(toolTip, GetPointDisplayText(input.SerializedValue))),
            InputEditorKind.Geometry => CreateInputBlock(objectId, step, input, CreateGeometryEditor(objectId, step, input), AppendToolTip(toolTip, GetGeometryDisplayText(input))),
            _ => CreateInputBlock(objectId, step, input, CreateTextEditor(objectId, step, input), toolTip),
        };
    }

    private Control CreateToggleInputRow(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input, string? toolTip)
    {
        var toggle = new CheckBox
        {
            Checked = bool.TryParse(input.SerializedValue, out var boolValue) && boolValue,
            Enabled = IsInputEnabled(step, input),
            ToolTip = toolTip,
        };

        toggle.CheckedChanged += (_, _) => CommitInput(
            objectId,
            step.Index,
            input,
            toggle.Checked == true ? "true" : "false");

        var block = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items =
            {
                CreateInputHeader(objectId, step, input, toolTip),
                toggle,
            },
        };

        AddInputMessages(block, input);
        return block;
    }

    private Control CreateInputBlock(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input, Control editor, string? toolTip)
    {
        SetToolTip(editor, toolTip);

        var block = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items =
            {
                CreateInputHeader(objectId, step, input, toolTip),
                editor,
            },
        };

        AddInputMessages(block, input);
        return block;
    }

    private Control CreateInputHeader(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input, string? toolTip)
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new StackLayoutItem(new Label
                {
                    Text = input.Label,
                    Wrap = WrapMode.Word,
                    ToolTip = toolTip,
                }, true),
                CreateInputLinkButton(objectId, step, input),
            },
        };
    }

    private static void AddInputMessages(StackLayout block, ModifierStepInputPanelState input)
    {
        if (!string.IsNullOrWhiteSpace(input.LinkStatusMessage))
        {
            block.Items.Add(CreateMessageLabel(input.LinkStatusMessage, isError: input.IsLinkBroken));
        }

        if (input.IsMissingRequiredValue)
        {
            block.Items.Add(CreateMessageLabel(input.ValidationMessage, isError: true));
        }
    }

    private Button CreateInputLinkButton(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var linkButton = new Button
        {
            Text = input.HasLink ? "●" : "○",
            Width = LinkButtonWidth,
            ToolTip = input.HasLink
                ? "Linked input. Click to change or clear the active link."
                : "Link this input to an upstream modifier output.",
        };

        linkButton.Click += (_, _) =>
        {
            var menu = BuildInputLinkMenu(objectId, step, input);
            menu.Show(linkButton);
        };

        return linkButton;
    }

    private ContextMenu BuildInputLinkMenu(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var menu = new ContextMenu();

        var clearItem = new ButtonMenuItem
        {
            Text = "Use manual value",
            Enabled = input.HasLink,
        };
        clearItem.Click += (_, _) => ClearInputLink(objectId, step.Index, input.Id);
        menu.Items.Add(clearItem);

        menu.Items.Add(new SeparatorMenuItem());

        if (input.AvailableLinks.Count == 0)
        {
            menu.Items.Add(new ButtonMenuItem
            {
                Text = "No compatible upstream outputs",
                Enabled = false,
            });
            return menu;
        }

        foreach (var optionGroup in input.AvailableLinks
                     .GroupBy(option => new { option.SourceStepId, option.SourceStepIndex, option.SourceStepLabel })
                     .OrderBy(group => group.Key.SourceStepIndex))
        {
            var groupItem = new ButtonMenuItem
            {
                Text = $"{optionGroup.Key.SourceStepIndex + 1}. {optionGroup.Key.SourceStepLabel}",
            };

            foreach (var option in optionGroup.OrderBy(candidate => candidate.SourceOutputLabel, StringComparer.OrdinalIgnoreCase))
            {
                var optionItem = new ButtonMenuItem
                {
                    Text = FormatInputLinkOption(option),
                };
                optionItem.Click += (_, _) => SetInputLink(objectId, step.Index, input.Id, option);
                groupItem.Items.Add(optionItem);
            }

            menu.Items.Add(groupItem);
        }

        return menu;
    }

    private static string FormatInputLinkOption(ModifierInputLinkOptionPanelState option)
    {
        var prefix = option.IsSelected ? "[current] " : string.Empty;
        if (option.HasRuntimeValue)
        {
            return $"{prefix}{option.SourceOutputLabel}  {option.RuntimeDisplayValue}";
        }

        return $"{prefix}{option.SourceOutputLabel}  (no runtime value yet)";
    }

    private static InputEditorKind GetEditorKind(ModifierStepInputPanelState input)
    {
        return input.Kind switch
        {
            ModifierIoKind.Boolean => InputEditorKind.Toggle,
            ModifierIoKind.NumberSlider => InputEditorKind.Slider,
            ModifierIoKind.Number when input.Minimum.HasValue && input.Maximum.HasValue => InputEditorKind.Slider,
            ModifierIoKind.Number => InputEditorKind.Number,
            ModifierIoKind.Point => InputEditorKind.Point,
            ModifierIoKind.Geometry => InputEditorKind.Geometry,
            _ => InputEditorKind.Text,
        };
    }

    private static Control CreateSliderEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        if (!TryCreateSliderConfiguration(input, out var configuration))
        {
            return CreateNumericEditor(objectId, step, input);
        }

        var initialValue = configuration.GetValue(configuration.GetSliderValue(GetInitialNumericValue(input)));
        var lastCommittedValue = SerializeNumber(initialValue, input.DecimalPlaces);
        var toolTip = AppendToolTip(
            BuildInputToolTip(input),
            $"{FormatDisplayNumber(configuration.Minimum, input.DecimalPlaces)} to {FormatDisplayNumber(configuration.Maximum, input.DecimalPlaces)}");

        var slider = new Slider
        {
            MinValue = 0,
            MaxValue = configuration.Steps,
            Value = configuration.GetSliderValue(initialValue),
            Enabled = IsInputEnabled(step, input),
            ToolTip = toolTip,
        };

        void CommitSliderValue()
        {
            var actualValue = configuration.GetValue(slider.Value);
            var serializedValue = SerializeNumber(actualValue, input.DecimalPlaces);
            if (string.Equals(serializedValue, lastCommittedValue, StringComparison.Ordinal))
            {
                return;
            }

            lastCommittedValue = serializedValue;
            CommitInput(objectId, step.Index, input, serializedValue);
        }

        slider.MouseUp += (_, _) => CommitSliderValue();
        slider.LostFocus += (_, _) => CommitSliderValue();

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Items =
            {
                slider,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Items =
                    {
                        new Label
                        {
                            Text = FormatDisplayNumber(configuration.Minimum, input.DecimalPlaces),
                        },
                        new StackLayoutItem(new Panel(), true),
                        new Label
                        {
                            Text = FormatDisplayNumber(configuration.Maximum, input.DecimalPlaces),
                        },
                    },
                },
            },
        };
    }

    private static Control CreateNumericEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var numericEditor = new NumericStepper
        {
            DecimalPlaces = input.DecimalPlaces,
            Increment = GetIncrement(input.DecimalPlaces),
            Enabled = IsInputEnabled(step, input),
            Width = 120,
        };

        if (input.Minimum.HasValue)
        {
            numericEditor.MinValue = input.Minimum.Value;
        }

        if (input.Maximum.HasValue)
        {
            numericEditor.MaxValue = input.Maximum.Value;
        }

        numericEditor.Value = GetInitialNumericValue(input);

        numericEditor.ValueChanged += (_, _) => CommitInput(
            objectId,
            step.Index,
            input,
            SerializeNumber(numericEditor.Value, input.DecimalPlaces));

        return numericEditor;
    }

    private static Control CreateTextEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var textEditor = new TextBox
        {
            Text = input.SerializedValue,
            ReadOnly = input.IsReadOnly,
            Enabled = IsInputEnabled(step, input),
        };

        textEditor.LostFocus += (_, _) => CommitInput(objectId, step.Index, input, textEditor.Text ?? string.Empty);
        return textEditor;
    }

    private static Control CreatePointEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var pickPointButton = new Button
        {
            Text = string.IsNullOrWhiteSpace(input.SerializedValue) ? "Set Point" : "Update Point",
            Enabled = IsInputEnabled(step, input),
        };

        pickPointButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            Point3d point;
            if (!TryGetSelectedPoint(doc, objectId, out point))
            {
                var rc = RhinoGet.GetPoint("Set point input", false, out point);
                if (rc != Rhino.Commands.Result.Success)
                {
                    return;
                }
            }

            var serializedValue = SerializePoint(point);
            if (!HelloRhinoCommonPlugin.Instance.Engine.SetStepInputValue(doc, objectId, step.Index, input.Id, serializedValue, out var message))
            {
                MessageBox.Show(message, MessageBoxType.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        };

        return pickPointButton;
    }

    private static Control CreateGeometryEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var pickGeometryButton = new Button
        {
            Text = string.IsNullOrWhiteSpace(input.SerializedValue) ? "Pick Geometry" : "Update Geometry",
            Enabled = IsInputEnabled(step, input),
            ToolTip = GetGeometryDisplayText(input),
        };

        pickGeometryButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            var previousSelection = GetSelectedObjectIds(doc);
            try
            {
                doc.Objects.UnselectAll();
                var rc = RhinoGet.GetMultipleObjects("Select geometry for input", false, Rhino.DocObjects.ObjectType.AnyObject, out var objRefs);
                if (rc != Rhino.Commands.Result.Success || objRefs is null || objRefs.Length == 0)
                {
                    return;
                }

                var ids = string.Join(" ", objRefs.Select(r => r.ObjectId.ToString()));
                if (!HelloRhinoCommonPlugin.Instance.Engine.SetStepInputValue(doc, objectId, step.Index, input.Id, ids, out var message))
                {
                    MessageBox.Show(message, MessageBoxType.Error);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(message))
                {
                    RhinoApp.WriteLine(message);
                }
            }
            finally
            {
                RestoreSelection(doc, previousSelection);
            }
        };

        if (!input.ShowModifiedGeometryToggle)
        {
            return pickGeometryButton;
        }

        var modifiedGeometryToggle = new CheckBox
        {
            Text = "Use modified geometry",
            Checked = input.UseModifiedGeometry,
            Enabled = step.Enabled && input.ModifiedGeometrySourceObjectId.HasValue,
        };
        modifiedGeometryToggle.CheckedChanged += (_, _) => ToggleModifiedGeometry(objectId, step.Index, input, modifiedGeometryToggle.Checked == true);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items =
            {
                pickGeometryButton,
                modifiedGeometryToggle,
            },
        };
    }

    private static Control CreateOutputSummaryRow(IReadOnlyList<ModifierStepOutputPanelState> outputs)
    {
        var summary = new Label
        {
            Text = string.Join("   ", outputs.Select(output => $"{output.Label}: {output.DisplayValue}")),
            Wrap = WrapMode.Word,
        };
        var toolTip = BuildOutputToolTip(outputs);
        SetToolTip(summary, toolTip);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items =
            {
                new Label
                {
                    Text = "Outputs",
                    ToolTip = toolTip,
                },
                summary,
            },
        };
    }

    private static void SetToolTip(Control control, string? toolTip)
    {
        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            control.ToolTip = toolTip;
        }
    }

    private static string? BuildInputToolTip(ModifierStepInputPanelState input)
    {
        return string.IsNullOrWhiteSpace(input.Description)
            ? null
            : input.Description;
    }

    private static string? BuildOutputToolTip(IReadOnlyList<ModifierStepOutputPanelState> outputs)
    {
        var descriptions = outputs
            .Where(output => !string.IsNullOrWhiteSpace(output.Description))
            .Select(output => $"{output.Label}: {output.Description}")
            .ToArray();

        return descriptions.Length == 0
            ? null
            : string.Join(Environment.NewLine, descriptions);
    }

    private static string? AppendToolTip(string? baseToolTip, string suffix)
    {
        if (string.IsNullOrWhiteSpace(baseToolTip))
        {
            return string.IsNullOrWhiteSpace(suffix) ? null : suffix;
        }

        return string.IsNullOrWhiteSpace(suffix)
            ? baseToolTip
            : $"{baseToolTip}{Environment.NewLine}{suffix}";
    }

    private static double GetIncrement(int decimalPlaces)
    {
        return decimalPlaces <= 0
            ? 1d
            : Math.Pow(10d, -decimalPlaces);
    }

    private static bool IsInputEnabled(ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        return step.Enabled && !input.IsReadOnly;
    }

    private static Guid[] GetSelectedObjectIds(RhinoDoc doc)
    {
        return doc.Objects.GetSelectedObjects(false, false)?.Select(candidate => candidate.Id).ToArray() ?? Array.Empty<Guid>();
    }

    private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        doc.Objects.UnselectAll();
        foreach (var objectId in objectIds)
        {
            doc.Objects.Select(objectId, true);
        }
    }

    private static bool TryGetSelectedPoint(RhinoDoc doc, Guid objectId, out Point3d point)
    {
        point = Point3d.Unset;
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject?.Geometry is not Rhino.Geometry.Point pointGeometry)
        {
            return false;
        }

        point = pointGeometry.Location;
        return true;
    }

    private static string GetPointDisplayText(string serializedValue)
    {
        return string.IsNullOrWhiteSpace(serializedValue)
            ? "No point set."
            : $"Current point: {serializedValue}";
    }

    private static string GetGeometryDisplayText(ModifierStepInputPanelState input)
    {
        if (input.HasLink)
        {
            return "Using linked geometry.";
        }

        return string.IsNullOrWhiteSpace(input.SerializedValue)
            ? "Using scene geometry by default."
            : $"Custom geometry: {input.SerializedValue}";
    }

    private static bool TryCreateSliderConfiguration(ModifierStepInputPanelState input, out NumericSliderConfiguration configuration)
    {
        configuration = default;
        if (!input.Minimum.HasValue || !input.Maximum.HasValue)
        {
            return false;
        }

        var minimum = input.Minimum.Value;
        var maximum = input.Maximum.Value;
        if (maximum < minimum)
        {
            (minimum, maximum) = (maximum, minimum);
        }

        if (Math.Abs(maximum - minimum) < double.Epsilon)
        {
            maximum = minimum + GetIncrement(input.DecimalPlaces);
        }

        var rawSteps = (maximum - minimum) / GetIncrement(input.DecimalPlaces);
        var steps = rawSteps > 0d
            ? (int)Math.Min(MaxSliderResolution, Math.Ceiling(rawSteps))
            : 1;

        configuration = new NumericSliderConfiguration(minimum, maximum, Math.Max(1, steps), input.DecimalPlaces);
        return true;
    }

    private static double GetInitialNumericValue(ModifierStepInputPanelState input)
    {
        return TryParseNumber(input.SerializedValue, out var numericValue)
            ? numericValue
            : input.Minimum ?? 0d;
    }

    private static double RoundNumber(double value, int decimalPlaces)
    {
        return decimalPlaces <= 0
            ? Math.Round(value, 0, MidpointRounding.AwayFromZero)
            : Math.Round(value, decimalPlaces, MidpointRounding.AwayFromZero);
    }

    private static string SerializeNumber(double value, int decimalPlaces)
    {
        var roundedValue = RoundNumber(value, decimalPlaces);
        return decimalPlaces <= 0
            ? roundedValue.ToString("0", CultureInfo.InvariantCulture)
            : roundedValue.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
    }

    private static string SerializePoint(Point3d point)
    {
        return FormattableString.Invariant($"{point.X:0.###############},{point.Y:0.###############},{point.Z:0.###############}");
    }

    private static string FormatDisplayNumber(double value, int decimalPlaces)
    {
        var serialized = SerializeNumber(value, decimalPlaces);
        return decimalPlaces <= 0
            ? serialized
            : serialized.TrimEnd('0').TrimEnd('.');
    }

    private static bool TryParseNumber(string value, out double number)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static void CommitInput(Guid objectId, int stepIndex, ModifierStepInputPanelState input, string value)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.SetStepInputValue(doc, objectId, stepIndex, input.Id, value, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void SetInputLink(Guid objectId, int stepIndex, string inputId, ModifierInputLinkOptionPanelState option)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.SetStepInputLink(doc, objectId, stepIndex, inputId, option.SourceStepId, option.SourceOutputId, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void ToggleModifiedGeometry(Guid objectId, int stepIndex, ModifierStepInputPanelState input, bool useModifiedGeometry)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (useModifiedGeometry)
        {
            if (!input.ModifiedGeometrySourceObjectId.HasValue)
            {
                return;
            }

            if (!HelloRhinoCommonPlugin.Instance.Engine.SetStepInputObjectPreviewLink(doc, objectId, stepIndex, input.Id, input.ModifiedGeometrySourceObjectId.Value, out var message))
            {
                MessageBox.Show(message, MessageBoxType.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        }
        else
        {
            ClearInputLink(objectId, stepIndex, input.Id);
        }
    }

    private static void ClearInputLink(Guid objectId, int stepIndex, string inputId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.ClearStepInputLink(doc, objectId, stepIndex, inputId, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void RemoveStep(Guid objectId, int index)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        HelloRhinoCommonPlugin.Instance.Engine.RemoveStep(doc, objectId, index, out var message);
        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void MoveStep(Guid objectId, int index, int offset)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        HelloRhinoCommonPlugin.Instance.Engine.MoveStep(doc, objectId, index, offset, out var message);
        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void EditStepDefinition(string path)
    {
        if (!HelloRhinoCommonPlugin.Instance.Engine.OpenModifierDefinitionInGrasshopper(path, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }
}
