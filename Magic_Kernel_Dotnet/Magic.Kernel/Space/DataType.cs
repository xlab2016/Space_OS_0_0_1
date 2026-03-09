using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Space
{
    public enum DataType : int
    {
        Text = 1,
        Binary = 2,
        Json = 3,
        Base64 = 4,
        String = 5,
        Integer = 6,
        Float = 7,
        Date = 8,
        DateTime = 9,
        Boolean = 10,
        Time = 11,
        Array = 12,
        Image = 13,
    }
}
