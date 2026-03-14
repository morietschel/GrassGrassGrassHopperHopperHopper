using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private const int FieldLabelWidth = 108;
    private const int SliderValueWidth = 84;

    private readonly Label _selectionLabel;
    private readonly Label _statusLabel;
    private readonly Label _folderLabel;
    private readonly Scrollable _rowsScrollable;
    private readonly Button _addButton;
    private readonly Button _refreshButton;
    private readonly Button _importFolderButton;
    private readonly List<string> _importedDefinitionPaths = new();

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

    public ModifierStackPanel()
    {
        _selectionLabel = new Label
        {
            Text = "Select one object to edit its modifier stack.",
            Wrap = WrapMode.Word,
        };

        _statusLabel = new Label
        {
            Text = string.Empty,
            Wrap = WrapMode.Word,
        };

        _folderLabel = new Label
        {
            Text = "No folder selected.",
            Wrap = WrapMode.Word,
            TextColor = Eto.Drawing.Colors.Gray,
        };

        _rowsScrollable = new Scrollable
        {
            ExpandContentWidth = true,
        };

        _addButton = new Button
        {
            Text = "Add…",
        };
        _addButton.Click += OnAddClicked;

        _refreshButton = new Button
        {
            Text = "Refresh",
        };
        _refreshButton.Click += OnRefreshClicked;

        _importFolderButton = new Button
        {
            Text = "Import Folder…",
        };
        _importFolderButton.Click += OnImportFolderClicked;

        Content = new DynamicLayout
        {
            Padding = 10,
            Spacing = new Eto.Drawing.Size(8, 8),
            Rows =
            {
                _selectionLabel,
                _statusLabel,
                _folderLabel,
                _rowsScrollable,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items =
                    {
                        _addButton,
                        _refreshButton,
                        _importFolderButton,
                    },
                },
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

        if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok)
        {
            return;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.AddStep(doc, state.SelectedObjectId.Value, dialog.FileName, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
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

    private void OnImportFolderClicked(object? sender, EventArgs e)
    {
        using var dialog = new Eto.Forms.OpenFileDialog
        {
            Title = "Pick a file in the folder that contains .gh/.ghx files",
            MultiSelect = false,
        };
        dialog.Filters.Add(new FileFilter("Grasshopper Definitions", ".gh", ".ghx"));

        if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var folder = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show("Could not determine folder from selected file.", MessageBoxType.Error);
            return;
        }
        _folderLabel.Text = $"Selected folder: {folder}";
        _importedDefinitionPaths.Clear();

        try
        {
            var ghFiles = Directory.EnumerateFiles(folder, "*.gh", SearchOption.AllDirectories);
            var ghxFiles = Directory.EnumerateFiles(folder, "*.ghx", SearchOption.AllDirectories);
            _importedDefinitionPaths.AddRange(ghFiles);
            _importedDefinitionPaths.AddRange(ghxFiles);
            _importedDefinitionPaths.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to scan folder: {ex.Message}", MessageBoxType.Error);
            return;
        }

        if (_importedDefinitionPaths.Count == 0)
        {
            _folderLabel.Text = $"No .gh/.ghx files found in: {folder}";
        }

        RefreshView();
    }

    private void RefreshView()
    {
        var state = HelloRhinoCommonPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);

        _selectionLabel.Text = string.IsNullOrWhiteSpace(state.SelectionLabel)
            ? "Select one object to edit its modifier stack."
            : state.SelectionLabel;

        _statusLabel.Text = state.StatusMessage;
        _addButton.Enabled = state.CanEdit && state.SelectedObjectId.HasValue;
        _refreshButton.Enabled = state.CanEdit && state.SelectedObjectId.HasValue && state.Steps.Count > 0;

        var rows = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
        };

        if (_importedDefinitionPaths.Count > 0)
        {
            rows.Items.Add(new Label
            {
                Text = "Imported definitions (click an icon to add):",
                Wrap = WrapMode.Word,
                TextColor = Eto.Drawing.Colors.MediumBlue,
            });
            rows.Items.Add(CreateImportedDefinitionRow(state));
        }

        if (!state.CanEdit)
        {
            rows.Items.Add(new Label
            {
                Text = state.StatusMessage,
                Wrap = WrapMode.Word,
            });
        }
        else if (state.Steps.Count == 0)
        {
            rows.Items.Add(new Label
            {
                Text = "No modifiers attached yet.",
            });
        }
        else if (state.SelectedObjectId.HasValue)
        {
            for (var i = 0; i < state.Steps.Count; i++)
            {
                rows.Items.Add(CreateStepRow(state.SelectedObjectId.Value, state.Steps[i]));
            }
        }

        _rowsScrollable.Content = rows;
    }

    private Control CreateImportedDefinitionRow(ModifierPanelState state)
    {
        var layout = new DynamicLayout
        {
            Padding = 0,
            Spacing = new Eto.Drawing.Size(6, 6),
        };

        for (var i = 0; i < _importedDefinitionPaths.Count; i++)
        {
            if (i % 3 == 0)
            {
                layout.BeginHorizontal();
            }

            var path = _importedDefinitionPaths[i];
            var fileName = Path.GetFileName(path);
            var iconButton = new Button
            {
                Text = "🧩 " + fileName,
                ToolTip = path,
                Width = 200,
            };
            iconButton.Click += (_, _) => AddStepFromImportedDefinition(state, path);
            layout.Add(iconButton);

            if (i % 3 == 2 || i == _importedDefinitionPaths.Count - 1)
            {
                layout.EndHorizontal();
            }
        }

        return new Panel
        {
            Content = layout,
        };
    }

    private Control CreateStepRow(Guid objectId, ModifierStepPanelState step)
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

        var pathLabel = new Label
        {
            Text = step.DisplayName,
            ToolTip = step.FullPath,
            Wrap = WrapMode.Word,
        };

        var editButton = new Button
        {
            Text = "Edit",
            ToolTip = "Open this modifier definition in Grasshopper.",
            Enabled = !string.IsNullOrWhiteSpace(step.FullPath),
        };
        editButton.Click += (_, _) => EditStepDefinition(step.FullPath);

        var upButton = new Button
        {
            Text = "Up",
            Enabled = step.Index > 0,
        };
        upButton.Click += (_, _) => MoveStep(objectId, step.Index, -1);

        var downButton = new Button
        {
            Text = "Down",
        };
        downButton.Click += (_, _) => MoveStep(objectId, step.Index, 1);

        var removeButton = new Button
        {
            Text = "Remove",
        };
        removeButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            HelloRhinoCommonPlugin.Instance.Engine.RemoveStep(doc, objectId, step.Index, out var message);
            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        };

        var content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Padding = new Eto.Drawing.Padding(2, 4),
        };

        content.Items.Add(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                enabledCheckBox,
                new StackLayoutItem(pathLabel, true),
                editButton,
                upButton,
                downButton,
                removeButton,
            },
        });

        if (!string.IsNullOrWhiteSpace(step.ErrorMessage))
        {
            content.Items.Add(new Label
            {
                Text = step.ErrorMessage,
                TextColor = Eto.Drawing.Colors.OrangeRed,
                Wrap = WrapMode.Word,
            });
        }

        foreach (var input in step.Inputs)
        {
            content.Items.Add(CreateInputRow(objectId, step, input));
        }

        if (step.Outputs.Count > 0)
        {
            content.Items.Add(CreateOutputSummaryRow(step.Outputs));
        }

        return new Panel
        {
            Content = content,
        };
    }

    private void AddStepFromImportedDefinition(ModifierPanelState state, string path)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (!state.SelectedObjectId.HasValue)
        {
            MessageBox.Show("Select an object first to add a modifier step.", MessageBoxType.Warning);
            return;
        }

        if (!HelloRhinoCommonPlugin.Instance.Engine.AddStep(doc, state.SelectedObjectId.Value, path, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }

        RefreshView();
    }

    private Control CreateInputRow(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var editor = CreateInputEditor(objectId, step, input);
        var toolTip = BuildInputToolTip(input);
        SetToolTip(editor, toolTip);

        if (!input.IsMissingRequiredValue)
        {
            return CreateFormRow(input.Label, editor, toolTip);
        }

        var content = new DynamicLayout
        {
            Padding = 0,
            Spacing = new Eto.Drawing.Size(4, 2),
        };
        content.AddRow(editor);
        content.AddRow(new Label
        {
            Text = input.ValidationMessage,
            Wrap = WrapMode.Word,
            TextColor = Eto.Drawing.Colors.OrangeRed,
        });

        return CreateFormRow(input.Label, content, toolTip, isWarning: true);
    }

    private Control CreateInputEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        return GetEditorKind(input) switch
        {
            InputEditorKind.Toggle => CreateToggleEditor(objectId, step, input),
            InputEditorKind.Slider => CreateSliderEditor(objectId, step, input),
            InputEditorKind.Number => CreateNumericEditor(objectId, step, input),
            InputEditorKind.Point => CreatePointEditor(objectId, step, input),
            InputEditorKind.Geometry => CreateGeometryEditor(objectId, step, input),
            _ => CreateTextEditor(objectId, step, input),
        };
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

    private static Control CreateToggleEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var toggle = new CheckBox
        {
            Checked = bool.TryParse(input.SerializedValue, out var boolValue) && boolValue,
            Enabled = IsInputEnabled(step, input),
        };

        toggle.CheckedChanged += (_, _) => CommitInput(
            objectId,
            step.Index,
            input,
            toggle.Checked == true ? "true" : "false");

        return toggle;
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

        var valueEditor = new NumericStepper
        {
            DecimalPlaces = input.DecimalPlaces,
            Increment = GetIncrement(input.DecimalPlaces),
            MinValue = configuration.Minimum,
            MaxValue = configuration.Maximum,
            Value = initialValue,
            Width = SliderValueWidth,
            Enabled = IsInputEnabled(step, input),
            ToolTip = toolTip,
        };

        var isUpdating = false;

        void CommitSliderValue(double actualValue)
        {
            var serializedValue = SerializeNumber(actualValue, input.DecimalPlaces);
            if (string.Equals(serializedValue, lastCommittedValue, StringComparison.Ordinal))
            {
                return;
            }

            lastCommittedValue = serializedValue;
            CommitInput(objectId, step.Index, input, serializedValue);
        }

        void SyncSliderValue(bool commit)
        {
            if (isUpdating)
            {
                return;
            }

            isUpdating = true;
            try
            {
                var actualValue = configuration.GetValue(slider.Value);
                valueEditor.Value = actualValue;
                if (commit)
                {
                    CommitSliderValue(actualValue);
                }
            }
            finally
            {
                isUpdating = false;
            }
        }

        slider.ValueChanged += (_, _) => SyncSliderValue(commit: false);
        slider.MouseUp += (_, _) => SyncSliderValue(commit: true);
        slider.LostFocus += (_, _) => SyncSliderValue(commit: true);

        valueEditor.ValueChanged += (_, _) =>
        {
            if (isUpdating)
            {
                return;
            }

            isUpdating = true;
            try
            {
                var actualValue = RoundNumber(valueEditor.Value, input.DecimalPlaces);
                slider.Value = configuration.GetSliderValue(actualValue);
                valueEditor.Value = actualValue;
                CommitSliderValue(actualValue);
            }
            finally
            {
                isUpdating = false;
            }
        };

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new StackLayoutItem(slider, true),
                valueEditor,
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
        var pointValue = new TextBox
        {
            Text = GetPointDisplayText(input.SerializedValue),
            ReadOnly = true,
            Enabled = true,
        };

        var pickPointButton = new Button
        {
            Text = "Set point",
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

            pointValue.Text = serializedValue;
            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        };

        return CreatePickerEditor(pointValue, pickPointButton);
    }

    private static Control CreateGeometryEditor(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var geometryValue = new TextBox
        {
            Text = GetGeometryDisplayText(input.SerializedValue),
            ReadOnly = true,
            Enabled = true,
        };

        var pickGeometryButton = new Button
        {
            Text = "Pick geometry",
            Enabled = IsInputEnabled(step, input),
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

                geometryValue.Text = ids;
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

        return CreatePickerEditor(geometryValue, pickGeometryButton);
    }

    private static Control CreatePickerEditor(TextBox valueBox, Button actionButton)
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new StackLayoutItem(valueBox, true),
                actionButton,
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

        return CreateFormRow("Outputs", summary, toolTip);
    }

    private static Control CreateFormRow(string label, Control editor, string? toolTip = null, bool isWarning = false)
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items =
            {
                CreateFieldLabel(label, toolTip, isWarning),
                new StackLayoutItem(editor, true),
            },
        };
    }

    private static Label CreateFieldLabel(string label, string? toolTip, bool isWarning = false)
    {
        return new Label
        {
            Text = label,
            Width = FieldLabelWidth,
            Wrap = WrapMode.Word,
            ToolTip = toolTip,
            TextColor = isWarning ? Eto.Drawing.Colors.OrangeRed : Eto.Drawing.Colors.Black,
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
            ? "(not set)"
            : serializedValue;
    }

    private static string GetGeometryDisplayText(string serializedValue)
    {
        return string.IsNullOrWhiteSpace(serializedValue)
            ? "(scene geometry by default)"
            : serializedValue;
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
