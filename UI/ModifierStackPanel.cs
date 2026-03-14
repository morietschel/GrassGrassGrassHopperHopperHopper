using System;
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

        return new Panel
        {
            Content = rowLayout,
        };
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
