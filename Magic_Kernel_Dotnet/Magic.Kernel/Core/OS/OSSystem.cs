using System;
using Magic.Kernel.Devices;
using Magic.Kernel.Core;

namespace Magic.Kernel.Core.OS
{
    /// <summary>OS/runtime helpers (memory, etc.).</summary>
    public static class OSSystem
    {
        public const string SpaceDefaultPath = "c:/Space";

        /// <summary>Запуск ядра ОС: инициализация SpaceEnvironment (конфиг dev/release.json).</summary>
        public static void StartKernel()
        {
            SpaceEnvironment.StartKernel();
        }

        /// <summary>Проверяет доступность памяти для выделения requiredBytes. Возвращает null при успехе, иначе результат с InsufficientMemory.</summary>
        public static DeviceOperationResult? CheckMemoryAvailable(long requiredBytes)
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.TotalAvailableMemoryBytes >= requiredBytes)
                return null;
            return DeviceOperationResult.InsufficientMemory($"Required ~{requiredBytes} bytes, available ~{memoryInfo.TotalAvailableMemoryBytes}");
        }
    }
}
