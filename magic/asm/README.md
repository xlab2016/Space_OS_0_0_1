# Magic ASM

Расширенный ассемблер для SPACE-OS: полная совместимость с GAS (GNU Assembler) + макросы.

## Директивы

- **`print "строка"`** — выводит строку через BIOS int 0x10. Компилятор подставляет `mov $msg, %si; call print_string` и добавляет в конец файла метку с `.asciz` и тело `print_string`.

## Сборка (Linux)

```bash
cd magic/asm
make
# или: gcc -O2 -Wall -o magic-asm main.c
```

Или через Make (при сборке образа для x86):

```bash
make ARCH=x86 image
```

Сначала соберётся `magic-asm` из C, затем из `boot/bios/stage1.masm` получится `stage1.S` и далее `stage1.bin`.

## Использование

```bash
magic-asm input.masm output.S
```

Дальше `output.S` собирается обычным ассемблером (clang/gas).

## Формат

Всё, что не является директивой `print "..."`, копируется в вывод без изменений. Синтаксис — GAS (например `boot/bios/stage1.S`). Для MBR в конце файла должны быть `.org 510` и `.word 0xAA55`; инъекция данных и `print_string` вставляется перед `.org 510`.

## Язык

Компилятор написан на **C** (C99 + POSIX). Собирается под Linux: `make` или `gcc -O2 -o magic-asm main.c`. Один бинарник, без внешних зависимостей; внутри ОС можно собрать тем же компилятором (gcc/clang в userspace).
