int sqlite3_enable_load_extension(void *db, int onoff)
{
    (void)db;
    (void)onoff;
    return 1;
}

int sqlite3_load_extension(void *db, const char *file, const char *entry, char **error)
{
    (void)db;
    (void)file;
    (void)entry;
    if (error)
        *error = 0;
    return 1;
}
