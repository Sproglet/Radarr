"""Database adapter for the disposable edition smoke tests, never application data.

PostgreSQL is deliberately restricted to the dedicated internal Docker alias and
synthetic credentials. Only databases created by this instance are ever dropped.
"""

from contextlib import contextmanager
import json
import re
import sqlite3
import uuid


class TestDatabase:
    def __init__(self, config, stopped):
        self.config = config
        self.stopped = stopped
        self.postgres = False
        self.created = []
        self.options = {"host": "radarr-edition-postgres", "port": 5432,
                        "user": "postgres", "password": "synthetic-test-password", "connect_timeout": 10}

    def enable_postgres(self):
        import psycopg2
        from psycopg2 import sql
        assert not self.postgres and self.stopped()
        self.postgres = True
        prefix = "radarr_edition_test_" + uuid.uuid4().hex
        self.main = prefix + "_main"
        self.log = prefix + "_log"
        connection = psycopg2.connect(dbname="postgres", **self.options)
        connection.autocommit = True
        try:
            with connection.cursor() as cursor:
                for name in (self.main, self.log):
                    cursor.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(name)))
                    self.created.append(name)
        finally:
            connection.close()
        print("Created disposable PostgreSQL databases: " + ", ".join(self.created), flush=True)

    def environment(self):
        if not self.postgres:
            return {}
        return {"Radarr__Postgres__Host": self.options["host"], "Radarr__Postgres__Port": "5432",
                "Radarr__Postgres__User": "postgres", "Radarr__Postgres__Password": self.options["password"],
                "Radarr__Postgres__MainDb": self.main, "Radarr__Postgres__LogDb": self.log}

    @contextmanager
    def connect(self, read_only=False):
        assert read_only or self.stopped(), "Fixture writes require this temporary Radarr process to be stopped"
        if self.postgres:
            import psycopg2
            connection = psycopg2.connect(dbname=self.main, **self.options)
            connection.set_session(readonly=read_only)
        else:
            path = self.config / "radarr.db"
            connection = sqlite3.connect("file:" + str(path) + "?mode=ro", uri=True) if read_only else sqlite3.connect(path)
        fixture = FixtureConnection(connection, self.postgres)
        try:
            yield fixture
            if self.postgres and not read_only:
                fixture.advance_sequences()
            connection.commit()
        except BaseException:
            connection.rollback()
            raise
        finally:
            connection.close()

    def rows(self, table):
        assert re.fullmatch(r"[A-Za-z]+", table)
        with self.connect(read_only=True) as connection:
            cursor = connection.execute('SELECT * FROM "' + table + '" ORDER BY "Id"')
            columns = [column[0] for column in cursor.description]
            return [{key: json.dumps(value) if isinstance(value, (dict, list)) else value
                     for key, value in zip(columns, row)} for row in cursor.fetchall()]

    def cleanup(self):
        assert self.stopped()
        if not self.created:
            return
        import psycopg2
        from psycopg2 import sql
        connection = psycopg2.connect(dbname="postgres", **self.options)
        connection.autocommit = True
        try:
            with connection.cursor() as cursor:
                for name in list(self.created):
                    assert re.fullmatch(r"radarr_edition_test_[0-9a-f]{32}_(main|log)", name)
                    cursor.execute(sql.SQL("DROP DATABASE {} WITH (FORCE)").format(sql.Identifier(name)))
                    self.created.remove(name)
                    print("Removed disposable PostgreSQL database: " + name, flush=True)
        finally:
            connection.close()


class FixtureConnection:
    def __init__(self, connection, postgres):
        self.connection = connection
        self.postgres = postgres
        self.inserted = set()

    def execute(self, statement, parameters=()):
        if not self.postgres:
            return self.connection.execute(statement, parameters)
        # Static fixture SQL uses '?' placeholders; all values are bound separately.
        statement = statement.replace("?", "%s")
        insert = re.match(r'INSERT INTO "([A-Za-z]+)"\s*\(([^)]+)\)', statement)
        if insert and '"Id"' in insert.group(2):
            statement = re.sub(r'(\)\s*)VALUES\b', r'\1OVERRIDING SYSTEM VALUE VALUES', statement, count=1)
        cursor = self.connection.cursor()
        cursor.execute(statement, parameters)
        match = re.match(r'INSERT INTO "([A-Za-z]+)"', statement)
        if match:
            self.inserted.add(match.group(1))
        return cursor

    def advance_sequences(self):
        from psycopg2 import sql
        with self.connection.cursor() as cursor:
            for table in self.inserted:
                cursor.execute("SELECT pg_get_serial_sequence(%s, 'Id')", ('"' + table + '"',))
                sequence = cursor.fetchone()[0]
                if sequence:
                    cursor.execute(sql.SQL('SELECT COALESCE(MAX("Id"), 0) FROM {}').format(sql.Identifier(table)))
                    maximum = cursor.fetchone()[0]
                    cursor.execute("SELECT setval(%s, %s, %s)", (sequence, max(1, maximum), maximum > 0))
