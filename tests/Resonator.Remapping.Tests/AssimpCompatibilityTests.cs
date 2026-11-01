#if HAS_ASSIMP
extern alias legacy;
using System;
using System.Numerics;
using Flatweave.Adapters;
using OldMatrix = legacy::Assimp.Matrix4x4;
using OldVector = legacy::Assimp.Vector3D;
using OldQuaternion = legacy::Assimp.Quaternion;

/// <summary>Compare the adapters with the actual original managed Assimp API, without loading native assimp.</summary>
internal static class AssimpCompatibilityTests
{
    public static void Run()
    {
        foreach (var scale in new[] { Vector3.One, new Vector3(2, 3, 4), new Vector3(-1, 2, 3), new Vector3(-1, -2, -3), new Vector3(0, 2, 3) })
        foreach (var translation in new[] { Vector3.Zero, new Vector3(3, -7, 11) })
        foreach (var rotation in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(.3f, -.5f, .7f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) })
        {
            var oldRotation = new OldQuaternion(rotation.W, rotation.X, rotation.Y, rotation.Z);
            var oldTranslation = OldMatrix.FromTranslation(new OldVector(translation.X, translation.Y, translation.Z));
            var oldScale = OldMatrix.FromScaling(new OldVector(scale.X, scale.Y, scale.Z));
            OldMatrix oldRotationMatrix = oldRotation.GetMatrix();
            Equal(Raw(oldRotation.GetMatrix()), AssimpMath.Rotation(rotation));
            Equal(Raw(oldTranslation), AssimpMath.Translation(translation));
            Equal(Raw(oldScale), Matrix4x4.CreateScale(scale));
            var old = oldTranslation * oldRotationMatrix * oldScale;
            var value = AssimpMath.Multiply(AssimpMath.Multiply(AssimpMath.Translation(translation), AssimpMath.Rotation(rotation)), Matrix4x4.CreateScale(scale));
            Equal(Raw(old), value);
            if (scale.X != 0 && scale.Y != 0 && scale.Z != 0)
            {
                var inverted = old;
                inverted.Inverse();
                var modernInverse = value;
                AssimpMath.Inverse(ref modernInverse);
                Equal(Raw(inverted), modernInverse);
            }
            old.Decompose(out var oldS, out var oldQ, out var oldT);
            AssimpMath.Decompose(value, out var s, out var q, out var t);
            if (Vector3.Distance(new Vector3(oldT.X, oldT.Y, oldT.Z), t) > .0001f
                || Vector3.Distance(new Vector3(oldS.X, oldS.Y, oldS.Z), s) > .0001f
                || QuaternionDifference(new Quaternion(oldQ.X, oldQ.Y, oldQ.Z, oldQ.W), q) > .0001f)
                throw new Exception("Assimp decomposition differs");
            old.DecomposeNoScaling(out var oldUnscaledQ, out var oldUnscaledT);
            AssimpMath.DecomposeNoScaling(value, out var unscaledQ, out var unscaledT);
            var expectedQ = new Vector4(oldUnscaledQ.X, oldUnscaledQ.Y, oldUnscaledQ.Z, oldUnscaledQ.W);
            var actualQ = new Vector4(unscaledQ.X, unscaledQ.Y, unscaledQ.Z, unscaledQ.W);
            if (Vector3.Distance(new Vector3(oldUnscaledT.X, oldUnscaledT.Y, oldUnscaledT.Z), unscaledT) > .0001f
                || Math.Min(Vector4.Distance(expectedQ, actualQ), Vector4.Distance(expectedQ, -actualQ)) > .0001f)
                throw new Exception("Assimp no-scaling decomposition differs");
        }
        var singular = OldMatrix.FromScaling(new OldVector(0, 1, 1));
        singular.Inverse();
        var modernSingular = Matrix4x4.CreateScale(0, 1, 1);
        AssimpMath.Inverse(ref modernSingular);
        if (!float.IsNaN(singular.A1) || !float.IsNaN(modernSingular.M11)) throw new Exception("singular inversion behaviour differs");
        Console.WriteLine("PASS Assimp translation, rotation, scaling, multiplication, inversion and decomposition differential checks");
    }

    static Matrix4x4 Raw(OldMatrix m) => new(m.A1,m.A2,m.A3,m.A4,m.B1,m.B2,m.B3,m.B4,m.C1,m.C2,m.C3,m.C4,m.D1,m.D2,m.D3,m.D4);
    static float QuaternionDifference(Quaternion a, Quaternion b)
    {
        var x = new Vector4(a.X, a.Y, a.Z, a.W);
        var y = new Vector4(b.X, b.Y, b.Z, b.W);
        return Math.Min(Vector4.Distance(x, y), Vector4.Distance(x, -y));
    }
    static void Equal(Matrix4x4 a, Matrix4x4 b)
    {
        float[] x = [a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44];
        float[] y = [b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44];
        for (int i=0;i<x.Length;i++) if (!float.IsFinite(x[i]) || Math.Abs(x[i]-y[i]) > .0001f) throw new Exception("Assimp matrix differs at " + i);
    }
}
#endif
