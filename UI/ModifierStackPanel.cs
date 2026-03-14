using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Eto.Forms;
using HelloRhinoCommon.Models;
using Rhino;
using Rhino.UI;

namespace HelloRhinoCommon.UI;

[Guid("9D0B9A10-3A8E-46FA-B6C2-4E004A80A29B")]
public sealed class ModifierStackPanel : Panel
{
    private readonly Label _selectionLabel;
    private readonly Label _statusLabel;
    private readonly Scrollable _rowsScrollable;
    private readonly Button _addButton;
    private readonly Button _refreshButton;

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

        Content = new DynamicLayout
        {
            Padding = 10,
            Spacing = new Eto.Drawing.Size(8, 8),
            Rows =
            {
                _selectionLabel,
                _statusLabel,
                _rowsScrollable,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items =
                    {
                        _addButton,
                        _refreshButton,
                    },
                },
            },
        };

        HelloRhinoCommonPlugin.Instance.Engine.StateChanged += OnEngineStateChanged;
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

    private void RefreshView()
    {
        var state = HelloRhinoCommonPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);

        _selectionLabel.Text = string.IsNullOrWhiteSpace(state.SelectionLabel)
            ? "Select one object to edit its modifier stack."
            : state.SelectionLabel;

        _statusLabel.Text = state.StatusMessage;
        _addButton.Enabled = state.CanEdit && state.SelectedObjectId.HasValue;
        _refreshButton.Enabled = state.CanEdit && state.SelectedObjectId.HasValue && state.Steps.Count > 0;

        var rows = new DynamicLayout
        {
            Padding = 0,
            Spacing = new Eto.Drawing.Size(4, 4),
        };

        if (!state.CanEdit)
        {
            rows.AddRow(new Label
            {
                Text = state.StatusMessage,
                Wrap = WrapMode.Word,
            });
        }
        else if (state.Steps.Count == 0)
        {
            rows.AddRow(new Label
            {
                Text = "No modifiers attached yet.",
            });
        }
        else if (state.SelectedObjectId.HasValue)
        {
            foreach (var step in state.Steps)
            {
                rows.AddRow(CreateStepRow(state.SelectedObjectId.Value, step));
            }
        }

        rows.Add(null);
        _rowsScrollable.Content = rows;
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

        var errorLabel = new Label
        {
            Text = step.ErrorMessage,
            TextColor = Eto.Drawing.Colors.OrangeRed,
            Wrap = WrapMode.Word,
        };

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

        var rowLayout = new DynamicLayout
        {
            Padding = 6,
            Spacing = new Eto.Drawing.Size(6, 4),
        };
        rowLayout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                enabledCheckBox,
                new StackLayoutItem(pathLabel, true),
                upButton,
                downButton,
                removeButton,
            },
        });

        if (!string.IsNullOrWhiteSpace(step.ErrorMessage))
        {
            rowLayout.AddRow(errorLabel);
        }

        if (step.Inputs.Count > 0)
        {
            rowLayout.AddRow(new Label
            {
                Text = "Inputs",
            });

            foreach (var input in step.Inputs)
            {
                rowLayout.AddRow(CreateInputRow(objectId, step, input));
            }
        }

        if (step.Outputs.Count > 0)
        {
            rowLayout.AddRow(new Label
            {
                Text = "Outputs",
            });

            foreach (var output in step.Outputs)
            {
                rowLayout.AddRow(CreateOutputRow(output));
            }
        }

        return new Panel
        {
            Content = rowLayout,
        };
    }

    private Control CreateInputRow(Guid objectId, ModifierStepPanelState step, ModifierStepInputPanelState input)
    {
        var layout = new DynamicLayout
        {
            Padding = 0,
            Spacing = new Eto.Drawing.Size(4, 2),
        };

        Control editor;
        switch (input.Kind)
        {
            case ModifierIoKind.Boolean:
                var boolEditor = new CheckBox
                {
                    Checked = bool.TryParse(input.SerializedValue, out var boolValue) && boolValue,
                    Enabled = step.Enabled && !input.IsReadOnly,
                    Text = input.Label,
                };
                boolEditor.CheckedChanged += (_, _) => CommitInput(objectId, step.Index, input, boolEditor.Checked == true ? "true" : "false");
                editor = boolEditor;
                break;

            case ModifierIoKind.NumberSlider:
                var sliderEditor = new NumericStepper
                {
                    DecimalPlaces = input.DecimalPlaces,
                    Increment = GetIncrement(input.DecimalPlaces),
                    Enabled = step.Enabled && !input.IsReadOnly,
                };
                if (input.Minimum.HasValue)
                {
                    sliderEditor.MinValue = input.Minimum.Value;
                }

                if (input.Maximum.HasValue)
                {
                    sliderEditor.MaxValue = input.Maximum.Value;
                }

                sliderEditor.Value = TryParseNumber(input.SerializedValue, out var numericValue)
                    ? numericValue
                    : input.Minimum ?? 0d;
                sliderEditor.ValueChanged += (_, _) => CommitInput(
                    objectId,
                    step.Index,
                    input,
                    sliderEditor.Value.ToString(CultureInfo.InvariantCulture));
                editor = WrapLabeledEditor(input.Label, sliderEditor);
                break;

            default:
                var textEditor = new TextBox
                {
                    Text = input.SerializedValue,
                    ReadOnly = input.IsReadOnly,
                    Enabled = step.Enabled && !input.IsReadOnly,
                };
                textEditor.LostFocus += (_, _) => CommitInput(objectId, step.Index, input, textEditor.Text ?? string.Empty);
                editor = WrapLabeledEditor(input.Label, textEditor);
                break;
        }

        layout.AddRow(editor);
        if (!string.IsNullOrWhiteSpace(input.Description))
        {
            layout.AddRow(new Label
            {
                Text = input.Description,
                Wrap = WrapMode.Word,
                TextColor = Eto.Drawing.Colors.Gray,
            });
        }

        return new Panel
        {
            Content = layout,
        };
    }

    private static Control CreateOutputRow(ModifierStepOutputPanelState output)
    {
        var layout = new DynamicLayout
        {
            Padding = 0,
            Spacing = new Eto.Drawing.Size(4, 2),
        };

        layout.AddRow(new Label
        {
            Text = $"{output.Label}: {output.DisplayValue}",
            Wrap = WrapMode.Word,
            ToolTip = output.Description,
        });

        return new Panel
        {
            Content = layout,
        };
    }

    private static Control WrapLabeledEditor(string label, Control editor)
    {
        return new DynamicLayout
        {
            Padding = 0,
            Spacing = new Eto.Drawing.Size(4, 2),
            Rows =
            {
                new Label
                {
                    Text = label,
                    Wrap = WrapMode.Word,
                },
                editor,
            },
        };
    }

    private static double GetIncrement(int decimalPlaces)
    {
        return decimalPlaces <= 0
            ? 1d
            : Math.Pow(10d, -decimalPlaces);
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
}
