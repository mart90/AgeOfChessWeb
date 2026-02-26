class Square(object):
    def __init__(self):
        self.x = None
        self.y = None
        
        # m = Mine
        # t = Trees
        # r = Rocks
        self.type = None
        
        # k, q, r, n, b, or p
        self.piece_type = None
        
        self.is_white = None

        # Relevant if type == "m"
        # 0 is white, 1 is black
        self.owned_by = None

    def set_piece(self, piece_type, is_white):
        self.piece_type = piece_type
        self.is_white = is_white
        if self.type == "m":
            self.owned_by = 0 if is_white else 1
