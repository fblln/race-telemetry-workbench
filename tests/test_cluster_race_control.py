import unittest

from scripts.cluster_race_control import cluster, normalize_message_text


class NormalizeMessageTextTests(unittest.TestCase):
    def test_numbers_cars_and_laps_are_generalised(self):
        # Normalisation is what lets identical message shapes cluster together.
        self.assertEqual(normalize_message_text("CAR 44 (HAM) 5 SECOND PENALTY"), "car # (ham) # second penalty")
        self.assertEqual(normalize_message_text("YELLOW IN LAP 12 TURN 3"), "yellow in lap # turn #")

    def test_blank_and_none_collapse_to_empty(self):
        self.assertEqual(normalize_message_text(None), "")
        self.assertEqual(normalize_message_text("   "), "")


class ClusterTests(unittest.TestCase):
    SAMPLE = [
        ("a", "YELLOW FLAG IN SECTOR 1"),
        ("b", "YELLOW FLAG IN SECTOR 2"),
        ("c", "YELLOW FLAG IN SECTOR 1"),
        ("d", "CAR 44 5 SECOND TIME PENALTY"),
        ("e", "CAR 16 5 SECOND TIME PENALTY"),
        ("f", "DRS ENABLED"),
        ("g", "   "),
    ]

    def test_blank_messages_are_dropped(self):
        out = cluster(self.SAMPLE, n_clusters=3)
        self.assertNotIn("g", out)
        self.assertEqual(len(out), 6)

    def test_identical_messages_share_a_cluster(self):
        # "a" and "c" are byte-identical, so any clustering must co-locate them.
        out = cluster(self.SAMPLE, n_clusters=3)
        self.assertEqual(out["a"][0], out["c"][0])

    def test_every_assignment_has_int_cluster_and_terms(self):
        out = cluster(self.SAMPLE, n_clusters=3)
        for cluster_id, terms in out.values():
            self.assertIsInstance(cluster_id, int)
            self.assertTrue(terms)

    def test_empty_corpus_returns_empty(self):
        self.assertEqual(cluster([("x", ""), ("y", None)], n_clusters=3), {})


if __name__ == "__main__":
    unittest.main()
