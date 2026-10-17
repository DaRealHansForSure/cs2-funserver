using System.Numerics;
using Cs2ServerControl.LiveMap;
using Xunit;

namespace Cs2ServerControl.Tests;

public class CameraRigTests {
    private static CameraRig FittedRig() {
        var rig = new CameraRig();
        rig.Fit(new Vector3(-2000, -1000, -200), new Vector3(2000, 3000, 400), topView: false);
        rig.Update(0);
        return rig;
    }

    [Fact]
    public void FitLooksAtTheMapCenterFromTheDefaultAngle() {
        CameraRig rig = FittedRig();

        CameraPose pose = rig.GetPose();

        Assert.Equal(new Vector3(0, 1000, 100), pose.Position + pose.LookDirection);
        Assert.Equal(CameraRig.DefaultYaw, rig.Yaw);
        Assert.Equal(CameraRig.DefaultPitch, rig.Pitch);
        Assert.Equal(Vector3.UnitZ, pose.UpDirection);
    }

    [Fact]
    public void TopViewLooksStraightDownWithYAsScreenUp() {
        CameraRig rig = FittedRig();

        rig.SetTopView(animate: false);
        CameraPose pose = rig.GetPose();

        Vector3 look = Vector3.Normalize(pose.LookDirection);
        Assert.Equal(-1, look.Z, 3);
        Assert.Equal(0, pose.UpDirection.X, 3);
        Assert.Equal(1, pose.UpDirection.Y, 3);
    }

    [Fact]
    public void AnimatedActionsFinishAfter200Milliseconds() {
        CameraRig rig = FittedRig();

        rig.RotateBy(15, animate: true);
        rig.Update(0.1f);
        Assert.NotEqual(CameraRig.DefaultYaw + 15, rig.Yaw, 2);

        rig.Update(0.1f);
        Assert.Equal(CameraRig.DefaultYaw + 15, rig.Yaw, 2);
    }

    [Fact]
    public void PitchAndZoomStayInRange() {
        CameraRig rig = FittedRig();

        rig.PitchBy(500, animate: false);
        rig.ZoomBy(100, animate: false);
        Assert.Equal(CameraRig.MaxPitch, rig.Pitch);
        Assert.Equal(CameraRig.MaxZoom, rig.Zoom);

        rig.PitchBy(-500, animate: false);
        rig.ZoomBy(-100, animate: false);
        Assert.Equal(CameraRig.MinPitch, rig.Pitch);
        Assert.Equal(CameraRig.MinZoom, rig.Zoom);
    }

    [Fact]
    public void MoveForwardFollowsTheViewDirectionOnTheGround() {
        CameraRig rig = FittedRig();
        rig.SetTopView(animate: false);
        Vector3 before = rig.Target;

        rig.Move(0, 100, animate: false);

        // Yaw 0 in top view looks toward +Y.
        Assert.Equal(before.Y + 100, rig.Target.Y, 2);
        Assert.Equal(before.Z, rig.Target.Z, 2);
    }

    [Fact]
    public void AdoptTakesOverAnOutsideCameraPose() {
        CameraRig rig = FittedRig();
        CameraPose original = rig.GetPose();

        var other = new CameraRig();
        other.Fit(new Vector3(-2000, -1000, -200), new Vector3(2000, 3000, 400), topView: true);
        other.Adopt(original.Position, original.LookDirection);

        Assert.Equal(rig.Yaw, other.Yaw, 2);
        Assert.Equal(rig.Pitch, other.Pitch, 2);
        Assert.Equal(rig.Zoom, other.Zoom, 3);
    }
}
