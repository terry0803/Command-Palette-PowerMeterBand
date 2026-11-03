using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation.Collections;

namespace PowerMeterBand;

/// <summary>
/// ListItem that reserves a fixed Dock label width and uses tabular digits, matching the
/// built-in Performance Monitor bands. SDK 0.12 has no helpers for this yet, so the
/// well-known attribute keys are set directly.
/// </summary>
internal sealed partial class DockValueListItem : ListItem, IExtendedAttributesProvider
{
    private readonly ValueSet _attributes = new();

    // titleWidth is DIPs (double), "Nch" or "text:sample", like the toolkit's DockLabelWidth,
    // or null to let the label size itself.
    public DockValueListItem(ICommand command, object? titleWidth)
        : base(command)
    {
        if (titleWidth is not null)
        {
            _attributes["Microsoft.CommandPalette.Dock.TitleWidth"] = titleWidth;
        }

        _attributes["Microsoft.CommandPalette.Dock.TabularDigits"] = true;
    }

    // Re-implements the interface so the host sees these hints; hides CommandItem's own bag.
    public new IDictionary<string, object> GetProperties() => _attributes;
}

/// <summary>
/// ListItem whose Dock label grows with its text up to a maximum width, then gets an ellipsis.
/// </summary>
internal sealed partial class DockTextListItem : ListItem, IExtendedAttributesProvider
{
    private readonly ValueSet _attributes = new();

    public DockTextListItem(ICommand command, double maxWidthDips)
        : base(command)
    {
        _attributes["Microsoft.CommandPalette.Dock.MaxLabelWidth"] = maxWidthDips;
    }

    public new IDictionary<string, object> GetProperties() => _attributes;
}
