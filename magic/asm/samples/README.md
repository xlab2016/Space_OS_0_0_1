# Magic ASM — примеры

## Hello World

- **hello.masm** — выводит "Space OS World" через BIOS.
- Сборка даёт **output.elf** (исполняемый ELF), запуск — в QEMU.

---

## Сборка и запуск (Linux)

### 1. Собрать компилятор

```bash
cd magic/asm
make
```

### 2. Собрать пример в output.elf

```bash
cd magic/asm/samples
make
```

Получится **output.elf** (и промежуточный output.o).

### 3. Запустить

```bash
make run
```

Или вручную:

```bash
qemu-system-i386 -kernel output.elf -nographic
```

Должна появиться строка **Space OS World**, затем VM зависает на `hlt`.  
Выход из QEMU: **Ctrl+A**, затем **X**.

Если при `make run` экран пустой или вывод не тот, попробуйте запуск как образ диска (BIOS грузит сектор по 0x7C00):

```bash
make output.bin
make run-bios
```

---

## Цели Makefile

| Цель        | Действие                          |
|------------|------------------------------------|
| `make`     | Собрать **output.elf**             |
| `make run` | Запустить output.elf в QEMU (-kernel) |
| `make output.bin` | Собрать raw-образ сектора      |
| `make run-bios`   | Запустить output.bin в QEMU (-drive) |
| `make clean`      | Удалить output.o, output.elf, output.bin |

Нужно: **gcc**, **ld** (binutils), **qemu-system-i386**.
