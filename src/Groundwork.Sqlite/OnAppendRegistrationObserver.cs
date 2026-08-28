namespace Groundwork.Sqlite;

/// <summary>
/// Internal test seam invoked after an OnAppend participant registers and before it writes.
/// Production observers do not implement this interface, so normal command observation is unchanged.
/// </summary>
internal interface IOnAppendRegistrationObserver
{
    void OnAppendRegistered();
}
