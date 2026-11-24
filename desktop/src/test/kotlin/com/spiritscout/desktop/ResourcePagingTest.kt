package com.spiritscout.desktop

import com.spiritscout.desktop.model.ResourcePaging
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ResourcePagingTest {
    @Test
    fun `skill pages advance until the backend reports no more`() {
        assertEquals(100, ResourcePaging.nextOffset(0, 100, 100, true))
        assertEquals(200, ResourcePaging.nextOffset(100, 100, 100, true))
        assertNull(ResourcePaging.nextOffset(200, 100, 98, false))
    }

    @Test
    fun `empty pages terminate a paged request`() {
        assertNull(ResourcePaging.nextOffset(0, 100, 0, true))
    }
}
