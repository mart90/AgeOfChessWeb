class Square(object):
    def __init__(self, id, x, y):
        self.x = x
        self.y = y
        self.id = id
        
        # m = Mine
        # t = Trees
        # r = Rocks
        self.terrain_type = None
        
        # k, q, r, n, b, or p
        self.piece_type = None        
        self.piece_is_white = None

        self.has_treasure = False

        # Relevant if type == "m"
        # 0 is white, 1 is black
        self.owned_by = None

    def set_piece(self, piece_type, is_white):
        self.piece_type = piece_type
        self.piece_is_white = is_white
        if self.type == "m":
            self.owned_by = 0 if is_white else 1

    # Returns 0 if not a blocker, 1 if full blocker, 2 if we can move here but no further
    def blocker_type(self, checking_for_white):
        if self.terrain_type == "r" or (self.piece_type is not None and self.piece_is_white == checking_for_white):
            return 1
        if self.terrain_type is not None or self.has_treasure or (self.piece_type is not None and self.piece_is_white != checking_for_white):
            return 2
        return 0
