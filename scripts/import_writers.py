"""Buffered row writers used by the FastF1 session importer."""

from __future__ import annotations

import argparse
import logging
import time
from typing import Any, Iterable, Sequence

def batched(values: Sequence[tuple[Any, ...]], batch_size: int) -> Iterable[Sequence[tuple[Any, ...]]]:
    for start in range(0, len(values), batch_size):
        yield values[start : start + batch_size]


def execute_many(connection: Any, sql: str, rows: Sequence[tuple[Any, ...]], batch_size: int) -> int:
    if not rows:
        return 0
    with connection.cursor() as cursor:
        for batch in batched(rows, batch_size):
            cursor.executemany(sql, batch)
    return len(rows)


class BatchWriter:
    """Bounded insert buffer for streaming large sample tables."""

    def __init__(self, connection: Any, sql: str, table_name: str, batch_size: int) -> None:
        self.connection = connection
        self.sql = sql
        self.table_name = table_name
        self.batch_size = batch_size
        self.buffer: list[tuple[Any, ...]] = []
        self.total = 0

    def add_many(self, rows: Sequence[tuple[Any, ...]]) -> None:
        if not rows:
            return
        self.buffer.extend(rows)
        while len(self.buffer) >= self.batch_size:
            self.flush(self.batch_size)

    def flush(self, limit: int | None = None) -> None:
        if not self.buffer:
            return
        if limit is None:
            batch = self.buffer
            self.buffer = []
        else:
            batch = self.buffer[:limit]
            self.buffer = self.buffer[limit:]
        execute_many(self.connection, self.sql, batch, len(batch))
        self.total += len(batch)
        logging.info("Inserted %s rows: %s", self.table_name, f"{self.total:,}")


class CopyWriter:
    """Bounded COPY buffer for append-heavy sample tables.

    COPY avoids the per-row INSERT protocol cost that dominates full-session
    imports. The writer keeps a compact key set because FastF1 can occasionally
    emit duplicate timestamps for the same driver inside raw source feeds.
    """

    def __init__(
        self,
        connection: Any,
        table_name: str,
        columns: Sequence[str],
        batch_size: int,
        key_indexes: tuple[int, ...],
    ) -> None:
        self.connection = connection
        self.table_name = table_name
        self.columns = columns
        self.batch_size = batch_size
        self.key_indexes = key_indexes
        self.buffer: list[tuple[Any, ...]] = []
        self.seen_keys: set[tuple[Any, ...]] = set()
        self.total = 0
        self.duplicates = 0
        self.write_seconds = 0.0

    def add_many(self, rows: Sequence[tuple[Any, ...]]) -> None:
        if not rows:
            return
        for row in rows:
            key = tuple(row[index] for index in self.key_indexes)
            if key in self.seen_keys:
                self.duplicates += 1
                continue
            self.seen_keys.add(key)
            self.buffer.append(row)
        while len(self.buffer) >= self.batch_size:
            self.flush(self.batch_size)

    def flush(self, limit: int | None = None) -> None:
        if not self.buffer:
            return
        if limit is None:
            batch = self.buffer
            self.buffer = []
        else:
            batch = self.buffer[:limit]
            self.buffer = self.buffer[limit:]

        start = time.perf_counter()
        column_sql = ", ".join(self.columns)
        with self.connection.cursor() as cursor:
            with cursor.copy(f"COPY {self.table_name} ({column_sql}) FROM STDIN") as copy:
                for row in batch:
                    copy.write_row(row)
        elapsed = time.perf_counter() - start
        self.write_seconds += elapsed
        self.total += len(batch)
        logging.info(
            "Copied %s rows: %s (+%s in %.2fs)",
            self.table_name,
            f"{self.total:,}",
            f"{len(batch):,}",
            elapsed,
        )


def sample_writer(
    connection: Any,
    args: argparse.Namespace,
    table_name: str,
    insert_sql: str,
    columns: Sequence[str],
) -> BatchWriter | CopyWriter:
    if args.sample_write_method == "copy":
        return CopyWriter(connection, table_name, columns, args.batch_size, (0, 1, 2))
    return BatchWriter(connection, insert_sql, table_name, args.batch_size)
