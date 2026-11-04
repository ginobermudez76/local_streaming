using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class InjectedInputTrackerTests
{
    [TestMethod]
    public void RecordKey_TracksAndUntracksProperly()
    {
        var tracker = new InjectedInputTracker();
        const int vkA = 0x41;
        const int vkCtrl = 0x11;

        Assert.IsFalse(tracker.IsKeyPressed(vkA));

        tracker.RecordKey(vkA, isDown: true);
        tracker.RecordKey(vkCtrl, isDown: true);

        Assert.IsTrue(tracker.IsKeyPressed(vkA));
        Assert.IsTrue(tracker.IsKeyPressed(vkCtrl));

        var snapshot = tracker.GetPressedKeysSnapshot();
        Assert.AreEqual(2, snapshot.Length);

        // Untrack A
        tracker.RecordKey(vkA, isDown: false);
        Assert.IsFalse(tracker.IsKeyPressed(vkA));
        Assert.IsTrue(tracker.IsKeyPressed(vkCtrl));

        snapshot = tracker.GetPressedKeysSnapshot();
        Assert.AreEqual(1, snapshot.Length);
        Assert.AreEqual(vkCtrl, snapshot[0]);
    }

    [TestMethod]
    public void RecordMouseButton_TracksAndUntracksProperly()
    {
        var tracker = new InjectedInputTracker();

        Assert.IsFalse(tracker.IsMouseButtonPressed(RemoteMouseButton.Left));

        tracker.RecordMouseButton(RemoteMouseButton.Left, isDown: true);
        tracker.RecordMouseButton(RemoteMouseButton.Right, isDown: true);

        Assert.IsTrue(tracker.IsMouseButtonPressed(RemoteMouseButton.Left));
        Assert.IsTrue(tracker.IsMouseButtonPressed(RemoteMouseButton.Right));

        var snapshot = tracker.GetPressedMouseButtonsSnapshot();
        Assert.AreEqual(2, snapshot.Length);

        tracker.RecordMouseButton(RemoteMouseButton.Left, isDown: false);
        Assert.IsFalse(tracker.IsMouseButtonPressed(RemoteMouseButton.Left));
        Assert.IsTrue(tracker.IsMouseButtonPressed(RemoteMouseButton.Right));

        tracker.Reset();
        Assert.AreEqual(0, tracker.GetPressedMouseButtonsSnapshot().Length);
    }

    [TestMethod]
    public void InvalidKeyCodes_IgnoredSafely()
    {
        var tracker = new InjectedInputTracker();

        tracker.RecordKey(0, isDown: true);
        tracker.RecordKey(-5, isDown: true);
        tracker.RecordKey(0x1000, isDown: true);

        Assert.AreEqual(0, tracker.GetPressedKeysSnapshot().Length);
    }

    [TestMethod]
    public void Concurrency_ThreadSafety()
    {
        var tracker = new InjectedInputTracker();

        Parallel.For(1, 100, i =>
        {
            tracker.RecordKey(i, isDown: true);
            tracker.RecordMouseButton(RemoteMouseButton.Left, isDown: true);
            var _ = tracker.GetPressedKeysSnapshot();
            tracker.RecordKey(i, isDown: false);
        });

        Assert.IsFalse(tracker.IsKeyPressed(1));
    }
}

