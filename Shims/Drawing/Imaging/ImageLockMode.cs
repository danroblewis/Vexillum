using System;

namespace System.Drawing.Imaging
{
    [Flags]
    public enum ImageLockMode
    {
        ReadOnly = 1,
        WriteOnly = 2,
        ReadWrite = 3,
        UserInputBuffer = 4
    }
}
