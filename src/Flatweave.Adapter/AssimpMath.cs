using System.Numerics;

namespace Flatweave.Adapters;

/// <summary>
/// AssimpNetter stores aiMatrix4x4 unchanged (column-vector convention). Numerics builders use
/// row vectors, so translation, rotation and decomposition explicitly transpose at this boundary.
/// Multiplication, decomposition and quaternion normalization also preserve the original managed
/// AssimpNet semantics; these are not interchangeable with the similarly named Numerics methods.
/// </summary>
public static class AssimpMath
{
    public static Matrix4x4 Translation(Vector3 value) => Matrix4x4.Transpose(Matrix4x4.CreateTranslation(value));
    public static Matrix4x4 Rotation(Quaternion value) => Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(value));
    public static void Inverse(ref Matrix4x4 value) => Matrix4x4.Invert(value, out value);
    // The original AssimpNet operator a*b evaluates the raw matrix product b*a.
    public static Matrix4x4 Multiply(Matrix4x4 left, Matrix4x4 right) => right * left;
    public static void Decompose(Matrix4x4 value, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
    {
        translation = new Vector3(value.M14, value.M24, value.M34);
        var x = new Vector3(value.M11, value.M12, value.M13);
        var y = new Vector3(value.M21, value.M22, value.M23);
        var z = new Vector3(value.M31, value.M32, value.M33);
        scale = new Vector3(x.Length(), y.Length(), z.Length());
        if (value.GetDeterminant() < 0) scale = -scale;
        if (scale.X != 0) x /= scale.X;
        if (scale.Y != 0) y /= scale.Y;
        if (scale.Z != 0) z /= scale.Z;
        rotation = RotationFromRaw(new Matrix4x4(x.X,x.Y,x.Z,0, y.X,y.Y,y.Z,0, z.X,z.Y,z.Z,0, 0,0,0,1));
    }

    public static void DecomposeNoScaling(Matrix4x4 value, out Quaternion rotation, out Vector3 translation)
    {
        translation = new Vector3(value.M14, value.M24, value.M34);
        rotation = RotationFromRaw(value);
    }

    static Quaternion RotationFromRaw(Matrix4x4 value)
    {
        var q = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(value));
        // The original wrapper divides by squared length, including when the input still has scale.
        float squared = q.LengthSquared();
        return squared == 0 ? q : new Quaternion(q.X / squared, q.Y / squared, q.Z / squared, q.W / squared);
    }
}
