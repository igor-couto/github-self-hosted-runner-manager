/* Test-only native listener: never contacts GitHub or runs jobs. */
#include <string.h>
#include <unistd.h>
#include <stdio.h>
int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "--version") == 0) { puts("2.337.0"); return 0; }
    if (argc == 2 && strcmp(argv[1], "run") == 0) { for (;;) pause(); }
    return 1;
}
