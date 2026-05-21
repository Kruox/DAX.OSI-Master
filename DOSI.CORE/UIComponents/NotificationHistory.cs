using System;
using System.Collections.Generic;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Process-wide ring-buffer of recent <see cref="DOSIPopNotification"/>
/// entries. The popup itself is transient (it animates in, sits for a few
/// seconds, then dismisses); this gives the user a "missed it" backstop -
/// a Notification Center can read this list and render every recent toast
/// even after the on-screen versions are long gone.
/// <para>
/// THREADING: writes happen on the UI thread (every Show call comes from
/// the dispatcher) but reads can come from anywhere a panel rebuilds, so
/// every public member locks. Snapshot semantics on read - the returned
/// list is a copy, safe to iterate while new entries are added.
/// </para>
/// </summary>
public static class NotificationHistory
{
    /// <summary>
    /// Maximum number of entries retained. Tuned so the bell-button popup
    /// can render the entire list without virtualization while still
    /// covering a typical multi-hour session's worth of toasts.
    /// </summary>
    public const int Capacity = 50;

    private static readonly LinkedList<NotificationRecord> _entries = new();
    private static readonly object _gate = new();

    /// <summary>Raised whenever <see cref="Add"/> appends a new entry.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Snapshot of the current history, newest first. Returns an empty
    /// array (never null) so call-sites can iterate without a null guard.
    /// </summary>
    public static IReadOnlyList<NotificationRecord> All
    {
        get
        {
            lock (_gate)
            {
                var arr = new NotificationRecord[_entries.Count];
                int i = 0;
                // LinkedList enumerates in insertion order (oldest -> newest);
                // reverse on copy so the consumer gets newest-first without
                // having to .Reverse() at every call-site.
                for (var node = _entries.Last; node != null; node = node.Previous)
                    arr[i++] = node.Value;
                return arr;
            }
        }
    }

    /// <summary>
    /// Appends an entry, evicting the oldest if the buffer is full. Safe
    /// to call from any thread; the <see cref="Changed"/> handler is
    /// invoked synchronously on the same thread that appended.
    /// </summary>
    public static void Add(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            _entries.AddLast(new NotificationRecord(text, DateTime.Now));
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Drops every retained entry. Used by sign-out so a new user starts clean.</summary>
    public static void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _entries.Count > 0;
            _entries.Clear();
        }
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>
/// One entry in <see cref="NotificationHistory"/>. Captured by value so
/// the snapshot semantics on <see cref="NotificationHistory.All"/> remain
/// safe across mutations on the live list.
/// </summary>
/// <param name="Text">The toast body text exactly as it was shown.</param>
/// <param name="WhenLocal">Local timestamp (for "5 min ago" rendering).</param>
public readonly record struct NotificationRecord(string Text, DateTime WhenLocal);
