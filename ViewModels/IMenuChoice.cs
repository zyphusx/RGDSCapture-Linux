namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// One entry in a menu that builds itself from a collection — the theme
    /// list and the UI-scale list.
    ///
    /// The two are unrelated otherwise, but a menu's container theme binds to
    /// whatever the items expose, and a compiled binding needs a type to
    /// resolve those names against. Naming the shape they already shared
    /// gives it one, so a renamed property fails the build rather than
    /// silently blanking a menu.
    /// </summary>
    public interface IMenuChoice
    {
        /// <summary>Label shown in the menu.</summary>
        string Name { get; }

        RelayCommand ApplyCommand { get; }

        /// <summary>Whether this entry is the one currently in force.</summary>
        bool IsActive { get; }
    }
}
