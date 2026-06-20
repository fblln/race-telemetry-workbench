#!/usr/bin/env python3
"""Cluster all race-control messages and write text_cluster/cluster_terms back to the DB.

Clustering needs the full corpus, so this runs across every session at once (not per-import).
Re-run after importing new sessions: `.venv/bin/python scripts/cluster_race_control.py`.
Requires migration db/migrations/009_race_control_clusters.sql to be applied first.

Algorithm mirrors the EDA notebook (cluster_race_control_text in
notebooks/database_surface_quality_support.py) — TF-IDF + KMeans — kept self-contained
here so the offline batch job doesn't pull the notebook's plotting/skrub dependency tree.
"""
import argparse
import os
import re

DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


def normalize_message_text(message) -> str:
    # ponytail: copied from EDA normalize_message_text; keep in sync if the EDA regex changes.
    text_value = "" if message is None else str(message).lower()
    text_value = re.sub(r"\bcar\s+\d+\b", "car #", text_value)
    text_value = re.sub(r"\bdriver\s+\d+\b", "driver #", text_value)
    text_value = re.sub(r"\blap\s+\d+\b", "lap #", text_value)
    text_value = re.sub(r"\bturn\s+\d+\b", "turn #", text_value)
    text_value = re.sub(r"\b\d+\b", "#", text_value)
    text_value = re.sub(r"\s+", " ", text_value)
    return text_value.strip()


def cluster(messages: list[tuple[str, str]], n_clusters: int) -> dict[str, tuple[int, str]]:
    """messages: (id, raw_message) -> {id: (cluster_id, cluster_terms)}."""
    norm = [(mid, normalize_message_text(msg)) for mid, msg in messages]
    norm = [(mid, text) for mid, text in norm if text]
    if not norm:
        return {}

    ids = [mid for mid, _ in norm]
    texts = [text for _, text in norm]
    max_clusters = max(2, min(n_clusters, len(set(texts)), len(texts)))

    try:
        import numpy as np
        from sklearn.cluster import KMeans
        from sklearn.feature_extraction.text import TfidfVectorizer

        vectorizer = TfidfVectorizer(min_df=2, ngram_range=(1, 2), stop_words="english")
        matrix = vectorizer.fit_transform(texts)
        model = KMeans(n_clusters=max_clusters, n_init=20, random_state=42)
        labels = model.fit_predict(matrix).astype(int)
        terms = np.array(vectorizer.get_feature_names_out())
        center_terms = {
            cid: ", ".join(terms[np.argsort(center)[-6:][::-1]])
            for cid, center in enumerate(model.cluster_centers_)
        }
        return {mid: (int(c), center_terms[int(c)]) for mid, c in zip(ids, labels)}
    except Exception as exc:  # sklearn missing or corpus too small for min_df
        print(f"  TF-IDF/KMeans unavailable ({exc}); falling back to normalized-text grouping.")
        distinct = {text: i for i, text in enumerate(sorted(set(texts)))}
        return {mid: (distinct[text], text) for mid, text in norm}


def self_check() -> None:
    msgs = [
        ("a", "YELLOW FLAG IN SECTOR 1"), ("b", "YELLOW FLAG IN SECTOR 1"),
        ("c", "CAR 44 5 SECOND TIME PENALTY"), ("d", "   "), ("e", "DRS ENABLED"),
    ]
    out = cluster(msgs, n_clusters=3)
    assert "d" not in out, "blank message must be dropped"
    assert out["a"][0] == out["b"][0], "identical messages must share a cluster"
    assert all(isinstance(c, int) and terms for c, terms in out.values()), "every row needs a cluster + terms"
    print("self-check passed")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database-url", default=database_url())
    parser.add_argument("--n-clusters", type=int, default=10)
    parser.add_argument("--self-check", action="store_true", help="Run an offline sanity check and exit.")
    args = parser.parse_args()

    if args.self_check:
        self_check()
        return

    import psycopg

    with psycopg.connect(args.database_url, autocommit=False) as connection:
        with connection.cursor() as cursor:
            cursor.execute("SELECT race_control_message_id, message FROM race_control_messages")
            rows = cursor.fetchall()
        print(f"Loaded {len(rows)} race-control messages.")

        assignments = cluster(rows, args.n_clusters)
        print(f"Assigned {len(assignments)} messages to {len({c for c, _ in assignments.values()})} clusters.")

        updates = [(cid, terms, mid) for mid, (cid, terms) in assignments.items()]
        with connection.cursor() as cursor:
            cursor.executemany(
                "UPDATE race_control_messages SET text_cluster = %s, cluster_terms = %s "
                "WHERE race_control_message_id = %s",
                updates,
            )
        connection.commit()
    print("Done.")


if __name__ == "__main__":
    main()
